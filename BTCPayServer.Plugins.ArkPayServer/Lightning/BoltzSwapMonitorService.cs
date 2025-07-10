using System.Collections.Concurrent;
using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Wallet.Boltz;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Hosted service that continuously monitors Boltz swaps and fires events when status changes occur.
/// This replaces the polling mechanism and provides a centralized way to track swap status.
/// </summary>
public class BoltzSwapMonitorService(IServiceProvider serviceProvider, ILogger<BoltzSwapMonitorService> logger)
    : IHostedService, IDisposable
{
    private BoltzWebsocketClient? _webSocketClient;
    private readonly ConcurrentDictionary<string, byte> _activeSwaps = new();
    private CancellationTokenSource? _cancellationTokenSource;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting Boltz swap monitoring service");
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        _ = InitiateMonitoring();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Boltz swap monitoring service");

        if (_cancellationTokenSource is not null)
        {
            await _cancellationTokenSource.CancelAsync();
        }

        if (_webSocketClient is not null)
        {
            await _webSocketClient.DisposeAsync();
        }
    }

    private async Task InitiateMonitoring()
    {
        if (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var dbContextFactory = scope.ServiceProvider.GetRequiredService<ArkPluginDbContextFactory>();
                
                await using var dbContext = dbContextFactory.CreateContext();
                
                // Get all active swaps that need monitoring
                var activeSwaps = await dbContext.LightningSwaps
                    .Where(s => s.Status == "created" || s.Status == "pending")
                    .ToListAsync(_cancellationTokenSource.Token);

                await MonitorSwaps(activeSwaps.Select(s => s.SwapId).ToArray());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error monitoring Boltz swaps");
            }
        }
    }

    public async Task MonitorSwaps(params string[] swapIds)
    {
        if (_webSocketClient == null)
        {
            // TODO: get URI this from BoltzClient instead
            var wsUri = new Uri("wss://api.boltz.exchange/v2/ws");
            _webSocketClient = await BoltzWebsocketClient.CreateAndConnectAsync(wsUri, _cancellationTokenSource?.Token ?? CancellationToken.None);
            _webSocketClient.OnAnyEventReceived += OnWebSocketEvent;
        }
        
        foreach (var swapId in swapIds)
        {
            _activeSwaps.TryAdd(swapId, 0);
        }
        await _webSocketClient.SubscribeAsync(swapIds, _cancellationTokenSource!.Token);
    }

    private async Task OnWebSocketEvent(WebSocketResponse response)
    {
        try
        {
            if (response.Event == "update" && response.Channel == "swap.update" && response.Args.Count > 0)
            {
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]?.ToString();
                    var status = swapUpdate["status"]?.ToString();
                    
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(status))
                    {
                        var evnt = new BoltzSwapStatusChangedEvent(id, status);
                        
                        using var scope = serviceProvider.CreateScope();
                        var dbContextFactory = scope.ServiceProvider.GetRequiredService<ArkPluginDbContextFactory>();
                        
                        await HandleReverseSwapUpdate(dbContextFactory, evnt);
                        
                        var eventAggregator = scope.ServiceProvider.GetRequiredService<EventAggregator>();
                        eventAggregator.Publish(new BoltzSwapStatusChangedEvent(id, status));
                        
                        // TODO: Unsubscribe for the swap, depending on the status
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing WebSocket event {@response}", response);
        }
    }
    
    private async Task HandleReverseSwapUpdate(ArkPluginDbContextFactory dbContextFactory, BoltzSwapStatusChangedEvent e)
    {
        var swapId = e.SwapId;
        var status = e.Status;
        
        logger.LogInformation("Processing reverse swap {SwapId} status update to: {Status}", swapId, status);
        
        try
        {
            await using var dbContext = dbContextFactory.CreateContext();
            
            // Find the swap in the database
            var swap = await dbContext.LightningSwaps
                .FirstOrDefaultAsync(s => s.SwapId == swapId && s.SwapType == "reverse");
                
            if (swap == null)
            {
                logger.LogWarning("Reverse swap {SwapId} not found in database", swapId);
                return;
            }
            
            // Update the swap status
            var oldStatus = swap.Status;
            swap.Status = status;
            
            // Set settlement time if swap is being marked as paid
            if (status == "invoice.paid" && swap.SettledAt == null)
            {
                swap.SettledAt = DateTimeOffset.UtcNow;
                logger.LogInformation("Reverse swap {SwapId} marked as settled", swapId);
            }
            
            await dbContext.SaveChangesAsync();
            
            logger.LogInformation("Updated reverse swap {SwapId} status from {OldStatus} to {NewStatus}", 
                swapId, oldStatus, status);
                
            // TODO: If status is "invoice.paid", trigger VTXO creation
            if (status == "invoice.paid")
            {
                logger.LogInformation("Reverse swap {SwapId} paid - VTXO creation should be triggered", swapId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process reverse swap {SwapId} status update", swapId);
            throw;
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _webSocketClient.DisposeAsync();
    }
}

public record BoltzSwapStatusChangedEvent(string SwapId, string Status);
