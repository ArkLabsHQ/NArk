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
    BoltzService boltzService,
    ArkPluginDbContextFactory dbContextFactory, 
    EventAggregator eventAggregator,
    ILogger<ArkLightningInvoiceListener> logger) : IExtendedLightningClient
{
    private readonly BoltzService _boltzService = boltzService;

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
        
        // var vtxo = await dbContext.Vtxos.FirstOrDefaultAsync(v => v.Script == reverseSwap.ContractScript, cancellationToken: cancellation);
        return Map(reverseSwap, network);
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
        
        // var vtxo = await dbContext.Vtxos.FirstOrDefaultAsync(v => v.Script == reverseSwap.ContractScript, cancellationToken: cancellation);
        return Map(reverseSwap, network);
    }

    public static LightningInvoice Map(LightningSwap reverseSwap, Network network)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(reverseSwap.Invoice, network); 
        
        // Map Boltz status to Lightning status
        var lightningStatus = reverseSwap.Status switch
        {
            "invoice.settled" => LightningInvoiceStatus.Paid,
            "invoice.expired" or "swap.expired" or "transaction.failed" or "transaction.refunded"=> LightningInvoiceStatus.Expired,
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
        
        // var vtxos = await dbContext.Vtxos.Where(v => scripts.Contains(v.Script)).ToDictionaryAsync(v => v.Script, v => v, cancellation);
        var invoices = new List<LightningInvoice>();
        foreach (var swap in reverseSwaps)
        {
            try
            {
                invoices.Add(Map(swap, network));
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
        throw new NotSupportedException();
        // await using var dbContext = dbContextFactory.CreateContext();
        //
        // var swap = await dbContext.LightningSwaps
        //     .Include(lightningSwap => lightningSwap.Contract)
        //     .FirstOrDefaultAsync(rs =>
        //         rs.SwapType == "submarine" &&
        //         rs.PreimageHash == paymentHash
        //         && rs.WalletId == walletId, cancellation);
        //
        //
        // return MapPayment(swap);
    }

    // private LightningPayment MapPayment(LightningSwap swap)
    // {
    //     var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, network); // TODO: DO not hard code
    //     var status = swap.Status switch
    //     {
    //         "transaction.claimed" or "invoice.paid" => LightningPaymentStatus.Complete,
    //         "invoice.failedToPay" or "swap.expired" or "transaction.lockupFailed" => LightningPaymentStatus.Failed,
    //         _ => LightningPaymentStatus.Pending
    //     };
    //     return new LightningPayment
    //     {
    //         Id = swap.SwapId,
    //         Amount = LightMoney.Satoshis(swap.OnchainAmount),
    //         Status = status,
    //         BOLT11 = swap.Invoice,
    //         PaymentHash = bolt11.PaymentHash?.ToString(),
    //         Preimage = swap.PreimageHash,
    //         CreatedAt = swap.CreatedAt
    //     };
    // }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
        // return ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
        // await using var dbContext = dbContextFactory.CreateContext();
        //
        // var swaps = await dbContext.LightningSwaps
        //     .Include(lightningSwap => lightningSwap.Contract)
        //     .Where(rs =>
        //         rs.SwapType == "submarine" &&
        //         rs.WalletId == walletId)
        //     
        //     .Skip((int)request.OffsetIndex.GetValueOrDefault(0))
        //     .ToListAsync(cancellation);
        //
        // return swaps.Select(MapPayment).ToArray();
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        var createInvoiceParams = new CreateInvoiceParams(amount, description, expiry);
        return await CreateInvoice(createInvoiceParams, cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
    {
        var swap = await _boltzService.CreateReverseSwap(walletId, createInvoiceRequest.Amount,
            cancellation);
        return Map(swap, network);
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        return Task.FromResult<ILightningInvoiceListener>(new ArkLightningInvoiceListener(walletId, logger, eventAggregator, network, cancellation));
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
            .Where(vtxo => vtxo.SpentByTransactionId == null || vtxo.SpentByTransactionId == "")
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

    public override string ToString() => $"type=arkade;wallet-id={walletId}";
}