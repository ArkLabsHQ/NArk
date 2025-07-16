using System.Threading.Channels;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Lightning.Events;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkLightningInvoiceListener : ILightningInvoiceListener 
{
    private readonly string _walletId;
    private readonly ILogger<ArkLightningInvoiceListener> _logger;
    private readonly Network _network;
    private readonly CancellationToken _cancellationToken;
    
    private readonly Channel<LightningInvoice> _paidInvoicesChannel = Channel.CreateUnbounded<LightningInvoice>();
    private readonly CompositeDisposable _leases = new();
    
    public ArkLightningInvoiceListener(
        string walletId,
        ILogger<ArkLightningInvoiceListener> logger,
        EventAggregator eventAggregator,
        Network network,
        CancellationToken cancellationToken) 
    {
        _walletId = walletId;
        _logger = logger;
        _network = network;
        _cancellationToken = cancellationToken;
        
        _leases.Add(eventAggregator.SubscribeAsync<LightningSwapUpdated>(OnInvoicePaid));
    }

    private async Task OnInvoicePaid(LightningSwapUpdated e)
    {
        if(e.Swap.WalletId != _walletId)
            return;
        var invoice = ArkLightningClient.Map(e.Swap, _network);
        if(invoice.Status != LightningInvoiceStatus.Paid)
            return;
        await _paidInvoicesChannel.Writer.WriteAsync(invoice, _cancellationToken);
    }

    public async Task<LightningInvoice?> WaitInvoice(CancellationToken cancellation)
    {
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellation);
        
        try
        {
            // Wait for a paid invoice from the channel
            while (await _paidInvoicesChannel.Reader.WaitToReadAsync(combinedCts.Token))
            {
                if (await _paidInvoicesChannel.Reader.ReadAsync(combinedCts.Token) is { } invoice)
                {
                    return invoice;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // This is expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for invoice in wallet {WalletId}", _walletId);
        }

        return null;
    }

    // private static LightningInvoice CreateLightningInvoiceFromSwap(LightningSwap swap)
    // {
    //     // Parse the BOLT11 invoice to get the amount and other details
    //     var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, Network.Main);
    //    
    //     return new LightningInvoice
    //     {
    //         Id = swap.SwapId,
    //         Amount = bolt11.MinimumAmount,
    //         Status = LightningInvoiceStatus.Paid,
    //         BOLT11 = swap.Invoice,
    //         PaidAt = swap.SettledAt,
    //         PaymentHash = bolt11.PaymentHash?.ToString(),
    //         Preimage = swap.PreimageHash
    //     };
    // }

    public void Dispose()
    {
        _leases.Dispose();
        _paidInvoicesChannel.Writer.Complete();
    }
}