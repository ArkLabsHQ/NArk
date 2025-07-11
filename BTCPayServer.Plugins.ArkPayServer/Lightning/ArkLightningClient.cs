using System.ComponentModel.DataAnnotations;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NArk;
using NArk.Services;
using NArk.Wallet.Boltz;
using NBitcoin;
using NBitcoin.DataEncoders;

using NodeInfo = BTCPayServer.Lightning.NodeInfo;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// ARK Lightning client – talks to a Boltz service instead of a real LN node.
/// Ark->LN interactions are handled through Boltz swaps
/// </summary>
public class ArkLightningClient(Network network, 
    string walletId, 
    BoltzClient boltzClient, 
    ArkPluginDbContextFactory dbContextFactory, 
    ArkWalletService walletService, 
    IOperatorTermsService operatorTermsService,
    BoltzSwapSubscriptionService boltzSubscriptionService,
    IServiceProvider serviceProvider) : IExtendedLightningClient
{
    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        // Find by payment hash - we need to parse all BOLT11 invoices to match
        var reverseSwap = await dbContext.LightningSwaps
            .FirstOrDefaultAsync(rs =>
                    rs.WalletId == walletId &&
                    rs.SwapType == "reverse" &&
                    rs.SwapId == invoiceId,
                cancellationToken: cancellation);

        if(reverseSwap == null)
            return null;
        
        var vtxo = await dbContext.Vtxos.FirstOrDefaultAsync(v => v.Script == reverseSwap.ContractScript, cancellationToken: cancellation);
        return CreateLightningInvoiceFromSwap(reverseSwap, vtxo);
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var paymentHashStr = paymentHash.ToString();
        // Find by payment hash - we need to parse all BOLT11 invoices to match
        var reverseSwap = await dbContext.LightningSwaps
            .FirstOrDefaultAsync(rs =>
                    rs.WalletId == walletId &&
                    rs.SwapType == "reverse" &&
                    rs.PreimageHash == paymentHashStr,
                cancellationToken: cancellation);

        if(reverseSwap == null)
            return null;
        
        var vtxo = await dbContext.Vtxos.FirstOrDefaultAsync(v => v.Script == reverseSwap.ContractScript, cancellationToken: cancellation);
        return CreateLightningInvoiceFromSwap(reverseSwap, vtxo);
    }

    public LightningInvoice CreateLightningInvoiceFromSwap(LightningSwap reverseSwap, VTXO? vtxo)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(reverseSwap.Invoice, network); 
        
        // Map Boltz status to Lightning status
        var lightningStatus = reverseSwap.Status switch
        {
            "invoice.set" or "invoice.paid" => LightningInvoiceStatus.Paid,
            "invoice.failedToPay" or "swap.expired" => LightningInvoiceStatus.Expired,
            _ => LightningInvoiceStatus.Unpaid
        };
        
        return new LightningInvoice
        {
            Id = reverseSwap.SwapId,
            Amount = LightMoney.Satoshis(reverseSwap.OnchainAmount),
            Status = lightningStatus,
            ExpiresAt = bolt11.ExpiryDate,
            BOLT11 = reverseSwap.Invoice,
            PaymentHash = reverseSwap.PreimageHash,
            PaidAt = reverseSwap.SettledAt,
            
            AmountReceived = lightningStatus == LightningInvoiceStatus.Paid ? LightMoney.Satoshis(reverseSwap.OnchainAmount) : null
        };
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {
        return await ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        
        var reverseSwaps = await dbContext.LightningSwaps
            .Where(rs => rs.SwapType == "reverse" &&  
                         rs.WalletId == walletId)
            .OrderByDescending(rs => rs.CreatedAt)
            .Skip((int)request.OffsetIndex.GetValueOrDefault(0))
            .ToListAsync(cancellation);
        
        var scripts = reverseSwaps.Select(rs => rs.ContractScript).ToArray();
        
        var vtxos = await dbContext.Vtxos.Where(v => scripts.Contains(v.Script)).ToDictionaryAsync(v => v.Script, v => v, cancellation);
        var invoices = new List<LightningInvoice>();
        foreach (var swap in reverseSwaps)
        {
            try
            {
                var invoice = CreateLightningInvoiceFromSwap(swap, vtxos.TryGet(swap.ContractScript));
                invoices.Add(invoice);
            }
            catch
            {
                // Skip failed invoices
            }
        }
        
        return invoices.ToArray();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        var swap = await dbContext.LightningSwaps
            .Include(lightningSwap => lightningSwap.Contract)
            .FirstOrDefaultAsync(rs =>
                rs.SwapType == "submarine" &&
                rs.PreimageHash == paymentHash
                && rs.WalletId == walletId, cancellation);

       
        return MapPayment(swap);
    }

    private LightningPayment? MapPayment(LightningSwap swap)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, network); // TODO: DO not hard code
        var status = swap.Status switch
        {
            "transaction.claimed" or "invoice.paid" => LightningPaymentStatus.Complete,
            "invoice.failedToPay" or "swap.expired" or "transaction.lockupFailed" => LightningPaymentStatus.Failed,
            _ => LightningPaymentStatus.Pending
        };
        return new LightningPayment
        {
            Id = swap.SwapId,
            Amount = LightMoney.Satoshis(swap.OnchainAmount),
            Status = status,
            BOLT11 = swap.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            Preimage = swap.PreimageHash,
            CreatedAt = swap.CreatedAt
        };
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        return ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        var swaps = await dbContext.LightningSwaps
            .Include(lightningSwap => lightningSwap.Contract)
            .Where(rs =>
                rs.SwapType == "submarine" &&
                rs.WalletId == walletId)
            
            .Skip((int)request.OffsetIndex.GetValueOrDefault(0))
            .ToListAsync(cancellation);
        
        return swaps.Select(MapPayment).ToArray();
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        var createInvoiceParams = new CreateInvoiceParams(amount, description, expiry);
        return await CreateInvoice(createInvoiceParams, cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        
        // Get the wallet from the database to extract the receiver key
        var wallet = await dbContext.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellation);
        if (wallet == null)
        {
            throw new InvalidOperationException($"Wallet with ID {walletId} not found");
        }
        
        // Extract the receiver key from the wallet - this is our claim public key
        
        var reverseSwapService = new BoltzSwapService(boltzClient, operatorTermsService);
        
        var invoiceAmountSats = createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);


        ReverseSwapResult? swapResult = null;
        var contract = await walletService.DeriveNewContract(walletId, async wallet =>
        {
            var receiverKey = wallet.PublicKey;

            // Create reverse swap with just the receiver key - sender key comes from Boltz response
            swapResult = await reverseSwapService.CreateReverseSwap(
                (long)invoiceAmountSats, 
                receiverKey,
                cancellationToken: cancellation);
            // Store the swap in the database with VHTLCContract information
            // First, create and save the ArkWalletContract
            var contractScript = swapResult.VHTLCContract.GetArkAddress().ScriptPubKey.ToHex();
            
            return (new ArkWalletContract
            {
                Script = contractScript,
                WalletId = walletId,
                Type = swapResult.VHTLCContract.Type,
                Active = true,
                ContractData = swapResult.VHTLCContract.GetContractData()
            }, swapResult.VHTLCContract);
        }, cancellation);

        if (swapResult is null || contract is not VHTLCContract htlcContract) 
        {
            return null;
        }       

        var contractScript = htlcContract.GetArkAddress().ScriptPubKey.ToHex();

        var reverseSwap = new LightningSwap
        {
            SwapId = swapResult.SwapId,
            WalletId = walletId,
            SwapType = "reverse",
            Invoice = swapResult.Invoice,
            LockupAddress = swapResult.LockupAddress,
            OnchainAmount = swapResult.OnchainAmount,
            TimeoutBlockHeight = swapResult.TimeoutBlockHeight,
            PreimageHash = Encoders.Hex.EncodeData(swapResult.PreimageHash),
            ClaimAddress = swapResult.ClaimAddress,
            ContractScript = contractScript, // Reference the contract by script
            Status = "created"
        };

        await dbContext.LightningSwaps.AddAsync(reverseSwap, cancellation);
        await dbContext.SaveChangesAsync(cancellation);
        
        await boltzSubscriptionService.MonitorSwaps(reverseSwap.SwapId);
        
        // TODO: I assume that the caller (in BTCPay) stores this invoice in the InvoiceRepository. Otherwise we need to do it
        return CreateLightningInvoiceFromSwap(reverseSwap, null);
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        var eventAggregator = serviceProvider.GetRequiredService<EventAggregator>();
        var logger = serviceProvider.GetRequiredService<ILogger<ArkLightningInvoiceListener>>();
        return Task.FromResult<ILightningInvoiceListener>(new ArkLightningInvoiceListener(walletId, dbContextFactory, logger, eventAggregator, cancellation));
    }

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        
        var contracts = await dbContext.WalletContracts
            .Where(c => c.WalletId == walletId).Select(c => c.Script).ToListAsync(cancellation);
        
        var vtxos = await dbContext.Vtxos
            .Where(vtxo => contracts.Contains(vtxo.Script))
            .Where(vtxo => vtxo.SpentByTransactionId == null)
            .ToListAsync(cancellation);
        
        var sum = vtxos.Sum(vtxo => vtxo.Amount);
        return new LightningNodeBalance()
        {
           OffchainBalance = new OffchainBalance()
           {
               Local = LightMoney.Satoshis(sum)
           }
        };
    }

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<ValidationResult?> Validate()
    {
        return Task.FromResult(ValidationResult.Success);
    }

    public string? DisplayName => "Arkade Lightning (Boltz)";
    public Uri? ServerUri => null;
}