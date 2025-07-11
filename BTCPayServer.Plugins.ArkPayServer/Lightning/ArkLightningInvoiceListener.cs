using System.Threading.Channels;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Lightning.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkLightningInvoiceListener : ILightningInvoiceListener 
{
    private readonly string _walletId;
    private readonly ArkPluginDbContextFactory _dbContextFactory;
    private readonly ILogger<ArkLightningInvoiceListener> _logger;
    private readonly CancellationToken _cancellationToken;
    
    private readonly Channel<LightningInvoice> _paidInvoicesChannel = Channel.CreateUnbounded<LightningInvoice>();
    private readonly CompositeDisposable _leases = new();
    
    public ArkLightningInvoiceListener(
        string walletId,
        ArkPluginDbContextFactory dbContextFactory,
        ILogger<ArkLightningInvoiceListener> logger,
        EventAggregator eventAggregator,
        CancellationToken cancellationToken) 
    {
        _walletId = walletId;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _cancellationToken = cancellationToken;
        
        _leases.Add(eventAggregator.SubscribeAsync<ArkLightningInvoicePaidEvent>(OnInvoicePaid));
    }

    private async Task OnInvoicePaid(ArkLightningInvoicePaidEvent e)
    {
        try
        {
            await using var dbContext = _dbContextFactory.CreateContext();
            var paidSwap = await dbContext.LightningSwaps
                .FirstOrDefaultAsync(s => s.SwapId == e.SwapId && s.WalletId == _walletId && !s.IsInvoiceReturned, _cancellationToken);

            if (paidSwap is null || paidSwap.WalletId != _walletId)
            {
                return;
            }
            
            var invoice = CreateLightningInvoiceFromSwap(paidSwap);
            await _paidInvoicesChannel.Writer.WriteAsync(invoice, _cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling swap status change for swap {SwapId}", e.SwapId);
        }
    }

    public async Task<LightningInvoice?> WaitInvoice(CancellationToken cancellation)
    {
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellation);
        
        try
        {
            // Wait for a paid invoice from the channel
            while (await _paidInvoicesChannel.Reader.WaitToReadAsync(combinedCts.Token))
            {
                if (_paidInvoicesChannel.Reader.TryRead(out var invoice))
                {
                    // Mark this invoice as returned in the database to prevent returning it again
                    try
                    {
                        await using var dbContext = _dbContextFactory.CreateContext();
                        var swap = await dbContext.LightningSwaps
                            .FirstOrDefaultAsync(s => s.SwapId == invoice.Id && s.WalletId == _walletId, combinedCts.Token);
                        
                        if (swap is { IsInvoiceReturned: false })
                        {
                            swap.IsInvoiceReturned = true;
                            await dbContext.SaveChangesAsync(combinedCts.Token);
                            return invoice;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error marking invoice {InvoiceId} as returned", invoice.Id);
                    }
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

    private static LightningInvoice CreateLightningInvoiceFromSwap(LightningSwap swap)
    {
        // Parse the BOLT11 invoice to get the amount and other details
        var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, Network.Main);
        
        return new LightningInvoice
        {
            Id = swap.SwapId,
            Amount = bolt11.MinimumAmount,
            Status = LightningInvoiceStatus.Paid,
            BOLT11 = swap.Invoice,
            PaidAt = swap.SettledAt,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            Preimage = swap.PreimageHash // This will be set when the swap is completed
        };
    }

    public void Dispose()
    {
        _leases.Dispose();
        _paidInvoicesChannel.Writer.Complete();
    }
}