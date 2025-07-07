using System.ComponentModel.DataAnnotations;
using System.Text.Json;
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
using NBitcoin.Secp256k1;
using NodeInfo = BTCPayServer.Lightning.NodeInfo;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// ARK Lightning client – talks to a Boltz service instead of a real LN node.
/// Ark->LN interactions are handled through Boltz swaps
/// </summary>
public class ArkLightningClient(string WalletId, BoltzClient BoltzClient, ArkPluginDbContextFactory DbContextFactory, LightningSwapProcessor SwapProcessor, IWalletService WalletService, IOperatorTermsService OperatorTermsService, IServiceProvider ServiceProvider) : IExtendedLightningClient
{
    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await using var dbContext = DbContextFactory.CreateContext();
        
        var reverseSwap = await dbContext.LightningSwaps
            .FirstOrDefaultAsync(rs => rs.SwapId == invoiceId, cancellation);
            
        if (reverseSwap == null)
            throw new ArgumentException($"Invoice with ID {invoiceId} not found");

        return CreateLightningInvoiceFromSwap(reverseSwap);
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        await using var dbContext = DbContextFactory.CreateContext();
        
        // Find by payment hash - we need to parse all BOLT11 invoices to match
        var reverseSwaps = await dbContext.LightningSwaps
            .Where(rs => rs.WalletId == WalletId)
            .ToListAsync(cancellation);
            
        foreach (var swap in reverseSwaps)
        {
            try
            {
                var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, Network.Main); // TODO: DO not hard code
                if (bolt11.PaymentHash == paymentHash)
                {
                    return CreateLightningInvoiceFromSwap(swap);
                }
            }
            catch
            {
                // Continue if parsing fails
            }
        }
        
        throw new ArgumentException($"Invoice with payment hash {paymentHash} not found");
    }

    public LightningInvoice CreateLightningInvoiceFromSwap(LightningSwap reverseSwap)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(reverseSwap.Invoice, Network.Main); // TODO: DO not hard code
        
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
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), // TODO: Some other value?
            BOLT11 = reverseSwap.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
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
        await using var dbContext = DbContextFactory.CreateContext();
        
        var query = dbContext.LightningSwaps.Where(rs => rs.WalletId == WalletId);
        if (request.OffsetIndex.HasValue)
        {
            query = query.Skip((int)request.OffsetIndex.Value);
        }
        
        var reverseSwaps = await query
            .OrderByDescending(rs => rs.CreatedAt)
            .Take(100) // TODO: Some other value?
            .ToListAsync(cancellation);
        
        var invoices = new List<LightningInvoice>();
        foreach (var swap in reverseSwaps)
        {
            try
            {
                var invoice = CreateLightningInvoiceFromSwap(swap);
                invoices.Add(invoice);
            }
            catch
            {
                // Skip failed invoices
            }
        }
        
        return invoices.ToArray();
    }

    public Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        var createInvoiceParams = new CreateInvoiceParams(amount, description, expiry);
        return await CreateInvoice(createInvoiceParams, cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
    {
        await using var dbContext = DbContextFactory.CreateContext();
        
        // Get the wallet from the database to extract the receiver key
        var wallet = await dbContext.Wallets.FirstOrDefaultAsync(w => w.Id == WalletId, cancellation);
        if (wallet == null)
        {
            throw new InvalidOperationException($"Wallet with ID {WalletId} not found");
        }
        
        // Extract the receiver key from the wallet - this is our claim public key
        var receiverKey = wallet.PublicKey;
        
        var reverseSwapService = new ReverseSwapService(BoltzClient, OperatorTermsService);
        
        var invoiceAmountSats = createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        
        // Create reverse swap with just the receiver key - sender key comes from Boltz response
        var swapResult = await reverseSwapService.CreateReverseSwapAsync(
            (long)invoiceAmountSats, 
            receiverKey,
            cancellationToken: cancellation);

        // Store the swap in the database with VHTLCContract information
        // First, create and save the ArkWalletContract
        var contractScript = swapResult.VHTLCContract.ToString();
        var walletContract = new ArkWalletContract
        {
            Script = contractScript,
            WalletId = WalletId,
            Type = "VHTLC",
            Active = true,
            ContractData = new Dictionary<string, string>
            {
                ["preimageHash"] = Encoders.Hex.EncodeData(swapResult.PreimageHash),
                ["claimAddress"] = swapResult.ClaimAddress ?? "",
                ["swapType"] = "reverse"
            }
        };

        // Add the contract if it doesn't already exist
        var existingContract = await dbContext.WalletContracts
            .FirstOrDefaultAsync(c => c.Script == contractScript, cancellation);
        
        if (existingContract == null)
        {
            dbContext.WalletContracts.Add(walletContract);
        }

        var reverseSwap = new LightningSwap
        {
            SwapId = swapResult.SwapId,
            WalletId = WalletId,
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

        dbContext.LightningSwaps.Add(reverseSwap);
        await dbContext.SaveChangesAsync(cancellation);

        // Parse the BOLT11 invoice to extract payment hash and create LightningInvoice
        var bolt11 = BOLT11PaymentRequest.Parse(swapResult.Invoice, Network.Main);
        
        return new LightningInvoice
        {
            Id = swapResult.SwapId,
            Amount = createInvoiceRequest.Amount,
            Status = LightningInvoiceStatus.Unpaid,
            ExpiresAt = DateTimeOffset.UtcNow.Add(createInvoiceRequest.Expiry),
            BOLT11 = swapResult.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            Preimage = Encoders.Hex.EncodeData(swapResult.Preimage)
        };
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        var swapMonitorService = ServiceProvider.GetRequiredService<BoltzSwapMonitorService>();
        var logger = ServiceProvider.GetRequiredService<ILogger<ArkInvoiceListener>>();
        return Task.FromResult<ILightningInvoiceListener>(new ArkInvoiceListener(WalletId, DbContextFactory, swapMonitorService, logger, cancellation));
    }

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        return Task.FromResult(new LightningNodeInformation
        {
            Alias = "Ark Lightning",
            Color = "FF0000",
            Version = "1.0.0",
            BlockHeight = 0
        });
    }

    public Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        // Ark balances are managed differently - return zero for now
        return Task.FromResult(new LightningNodeBalance
        {
            OffchainBalance = new OffchainBalance
            {
                Local = LightMoney.Zero,
                Remote = LightMoney.Zero
            },
            OnchainBalance = new OnchainBalance
            {
                Confirmed = Money.Zero,
                Unconfirmed = Money.Zero,
                Reserved = Money.Zero
            }
        });
    }

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        throw new NotSupportedException("Ark Lightning client does not support outgoing payments");
    }

    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        throw new NotSupportedException("Ark Lightning client does not support outgoing payments");
    }

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        throw new NotSupportedException("Ark Lightning client does not support outgoing payments");
    }

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default)
    {
        throw new NotSupportedException("Ark Lightning client does not support channel management");
    }

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
    {
        throw new NotSupportedException("Ark Lightning client does not support direct Bitcoin deposits");
    }

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
    {
        throw new NotSupportedException("Ark Lightning client does not support peer connections");
    }

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        // For now, mark as cancelled in our database
        return Task.CompletedTask;
    }

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
    {
        return Task.FromResult(Array.Empty<LightningChannel>());
    }

    public Task<ValidationResult?> Validate()
    {
        return Task.FromResult<ValidationResult?>(null);
    }

    public string? DisplayName => "Ark Lightning (Boltz)";
    public Uri? ServerUri => null;
}