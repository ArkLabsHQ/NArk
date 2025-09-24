using System.ComponentModel.DataAnnotations;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Contracts;
using NArk.Extensions;
using NArk.Services;
using NBitcoin;
using NodeInfo = BTCPayServer.Lightning.NodeInfo;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkLightningClient(
    IOperatorTermsService operatorTermsService,
    Network network,
    string walletId,
    BoltzService boltzService,
    ArkPluginDbContextFactory dbContextFactory,
    EventAggregator eventAggregator,
    ILogger<ArkLightningInvoiceListener> logger) : IExtendedLightningClient
{
    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var reverseSwap = await dbContext.Swaps
            .Include(s => s.Contract)
            .FirstOrDefaultAsync(rs =>
                    rs.WalletId == walletId &&
                    rs.SwapType == ArkSwapType.ReverseSubmarine &&
                    rs.SwapId == invoiceId,
                cancellationToken: cancellation);

        return reverseSwap == null ? null : Map(reverseSwap, network);
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var paymentHashStr = paymentHash.ToString();
        var reverseSwap = await dbContext.Swaps
            .Include(s => s.Contract)
            .FirstOrDefaultAsync(rs =>
                    rs.WalletId == walletId &&
                    rs.SwapType == ArkSwapType.ReverseSubmarine &&
                    rs.Hash == paymentHashStr,
                cancellationToken: cancellation);

        return reverseSwap == null ? null : Map(reverseSwap, network);
    }

    public static LightningInvoice Map(ArkSwap reverseSwap, Network network)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(reverseSwap.Invoice, network);
        var lightningStatus = reverseSwap.Status switch
        {
            ArkSwapStatus.Settled => LightningInvoiceStatus.Paid,
            ArkSwapStatus.Failed => LightningInvoiceStatus.Expired,
            ArkSwapStatus.Pending => LightningInvoiceStatus.Unpaid,
            _ => throw new NotSupportedException()
        };

        VHTLCContract? contract =
            ArkContract.Parse(reverseSwap.Contract.Type, reverseSwap.Contract.ContractData) as VHTLCContract;
        return new LightningInvoice
        {
            Id = reverseSwap.SwapId,
            Amount = bolt11.MinimumAmount,
            Status = lightningStatus,
            ExpiresAt = bolt11.ExpiryDate,
            BOLT11 = reverseSwap.Invoice,
            PaymentHash = bolt11.PaymentHash.ToString(),
            PaidAt = lightningStatus == LightningInvoiceStatus.Paid ? reverseSwap.UpdatedAt : null,
            // we have to comment this out because BTCPay will consider this invoice as partially paid..
            // AmountReceived = lightningStatus == LightningInvoiceStatus.Paid
            //     ? LightMoney.Satoshis(reverseSwap.ExpectedAmount)
            //     : null,
            Preimage = contract?.Preimage?.ToHex(),
        };
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {
        return await ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        var reverseSwaps = await dbContext.Swaps
            .Include(s => s.Contract)
            .Where(rs => rs.SwapType == ArkSwapType.ReverseSubmarine && rs.WalletId == walletId)
            .Where(rs =>
                request.PendingOnly == null || !request.PendingOnly.Value || rs.Status == ArkSwapStatus.Pending)
            .OrderByDescending(rs => rs.CreatedAt)
            .Skip((int) request.OffsetIndex.GetValueOrDefault(0))
            .ToListAsync(cancellation);

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
        await using var dbContext = dbContextFactory.CreateContext();
        
        var swap = await dbContext.Swaps
            .Include(lightningSwap => lightningSwap.Contract)
            .FirstOrDefaultAsync(rs =>
                rs.SwapType == ArkSwapType.Submarine &&
                rs.Hash == paymentHash
                && rs.WalletId == walletId, cancellation);
        return MapPayment(swap);
    }

    private LightningPayment MapPayment(ArkSwap swap)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, network);
        var status = swap.Status switch
        {
            ArkSwapStatus.Settled => LightningPaymentStatus.Complete,
            ArkSwapStatus.Failed => LightningPaymentStatus.Failed,
            ArkSwapStatus.Pending => LightningPaymentStatus.Pending,
            _ => LightningPaymentStatus.Unknown
        };
        var htlcContract = ArkContract.Parse(swap.Contract.Type, swap.Contract.ContractData) as VHTLCContract;
        
        return new LightningPayment
        {
            Id = swap.SwapId,
            Amount = bolt11.MinimumAmount,
            Status = status,
            BOLT11 = swap.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            Preimage = htlcContract?.Preimage?.ToHex(),
            CreatedAt = swap.CreatedAt,
            AmountSent = LightMoney.Satoshis(swap.ExpectedAmount),
        };
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        return ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        
        var swaps = await dbContext.Swaps
            .Include(lightningSwap => lightningSwap.Contract)
            .Where(rs =>
                rs.SwapType == ArkSwapType.Submarine &&
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

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = default)
    {
        var terms = await operatorTermsService.GetOperatorTerms(cancellation);
        if (terms.Dust > createInvoiceRequest.Amount)
        {
            throw new InvalidOperationException("Sub-dust amounts are not supported");
        }
        
        var swap = await boltzService.CreateReverseSwap(walletId, createInvoiceRequest,
            cancellation);
        return Map(swap, network);
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        return Task.FromResult<ILightningInvoiceListener>(
            new ArkLightningInvoiceListener(walletId, logger, eventAggregator, network, cancellation));
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
            .Where(vtxo => (vtxo.SpentByTransactionId == null || vtxo.SpentByTransactionId == "") && !vtxo.IsNote) 
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
        return Pay(null, payParams, cancellation);
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        try
        {
            if (string.IsNullOrEmpty(bolt11))
            {
                throw new NotSupportedException("BOLT11 is required");
            }
            
            
            var pr = BOLT11PaymentRequest.Parse(bolt11, network);
            var result = await boltzService.CreateSubmarineSwap(walletId, pr, cancellation);
        
            var payment = MapPayment(result);
            return new PayResponse()
            {
                Details = new PayDetails()
                {
                    PaymentHash = pr.PaymentHash,
                    Preimage = string.IsNullOrEmpty(payment.Preimage) ? null : new uint256(payment.Preimage),
                    Status = payment.Status,
                    FeeAmount = payment.Fee,
                    TotalAmount = payment.AmountSent
                }
            };
            
        }
        catch (Exception e)
        {
            return new PayResponse(PayResult.Error, e.Message);
        }
       
    }

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        return Pay(bolt11, new PayInvoiceParams(), cancellation);
    }

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = default)
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