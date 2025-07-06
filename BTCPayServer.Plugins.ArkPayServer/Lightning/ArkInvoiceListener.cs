using System.Threading.Channels;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkInvoiceListener : ILightningInvoiceListener
{
    private readonly string _walletId;
    private readonly ArkPluginDbContextFactory _dbContextFactory;
    private readonly BoltzSwapMonitorService _swapMonitorService;
    private readonly ILogger<ArkInvoiceListener> _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly Channel<LightningInvoice> _paidInvoicesChannel;
    private readonly ChannelWriter<LightningInvoice> _paidInvoicesWriter;
    private readonly ChannelReader<LightningInvoice> _paidInvoicesReader;
    private readonly HashSet<string> _returnedInvoiceIds = new();

    public ArkInvoiceListener(string walletId, ArkPluginDbContextFactory dbContextFactory, 
        BoltzSwapMonitorService swapMonitorService, ILogger<ArkInvoiceListener> logger,
        CancellationToken cancellationToken)
    {
        _walletId = walletId;
        _dbContextFactory = dbContextFactory;
        _swapMonitorService = swapMonitorService;
        _logger = logger;
        _cancellationToken = cancellationToken;
        
        // Create channel for paid invoices
        var channel = Channel.CreateUnbounded<LightningInvoice>();
        _paidInvoicesChannel = channel;
        _paidInvoicesWriter = channel.Writer;
        _paidInvoicesReader = channel.Reader;
        
        // Subscribe to swap status changes
        _swapMonitorService.SwapStatusChanged += OnSwapStatusChanged;
    }

    private async void OnSwapStatusChanged(object? sender, BoltzSwapStatusChangedEventArgs e)
    {
        // Only handle events for this wallet
        if (e.WalletId != _walletId)
            return;

        try
        {
            // If the swap is paid, add to paid invoices queue
            if (e.Status == "invoice.paid")
            {
                await using var dbContext = _dbContextFactory.CreateContext();
                var paidSwap = await dbContext.LightningSwaps
                    .FirstOrDefaultAsync(s => s.SwapId == e.SwapId && s.WalletId == _walletId, _cancellationToken);
                    
                if (paidSwap != null && !_returnedInvoiceIds.Contains(paidSwap.SwapId))
                {
                    var invoice = CreateLightningInvoiceFromSwap(paidSwap);
                    await _paidInvoicesWriter.WriteAsync(invoice, _cancellationToken);
                }
            }
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
            while (await _paidInvoicesReader.WaitToReadAsync(combinedCts.Token))
            {
                if (_paidInvoicesReader.TryRead(out var invoice))
                {
                    // Ensure we don't return the same invoice twice
                    if (!_returnedInvoiceIds.Contains(invoice.Id))
                    {
                        _returnedInvoiceIds.Add(invoice.Id);
                        return invoice;
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
        _swapMonitorService.SwapStatusChanged -= OnSwapStatusChanged;
        _paidInvoicesWriter.Complete();
    }
}