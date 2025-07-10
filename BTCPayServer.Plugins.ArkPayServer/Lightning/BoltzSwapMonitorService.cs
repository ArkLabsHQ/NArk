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
    private readonly ConcurrentDictionary<string, BoltzWebsocketClient> _webSocketClients = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _activeSwapsByWallet = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _monitoringTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting Boltz swap monitoring service");
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitoringTask = Task.Run(MonitorSwapsAsync, _cancellationTokenSource.Token);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Boltz swap monitoring service");
        
        _cancellationTokenSource?.Cancel();
        
        if (_monitoringTask != null)
        {
            await _monitoringTask.WaitAsync(cancellationToken);
        }

        // Dispose all WebSocket clients
        foreach (var client in _webSocketClients.Values)
        {
            await client.DisposeAsync();
        }
        _webSocketClients.Clear();
        _activeSwapsByWallet.Clear();
    }

    private async Task MonitorSwapsAsync()
    {
        while (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var dbContextFactory = scope.ServiceProvider.GetRequiredService<ArkPluginDbContextFactory>();
                
                await using var dbContext = dbContextFactory.CreateContext();
                
                // Get all active swaps that need monitoring
                var activeSwaps = await dbContext.LightningSwaps
                    .Where(s => s.Status == "created" || s.Status == "pending")
                    .GroupBy(s => s.WalletId)
                    .ToListAsync(_cancellationTokenSource.Token);

                // Update WebSocket subscriptions for each wallet
                foreach (var walletGroup in activeSwaps)
                {
                    var walletId = walletGroup.Key;
                    var swapIds = walletGroup.Select(s => s.SwapId).ToArray();
                    
                    await EnsureWebSocketConnection(walletId, swapIds);
                }

                // Remove inactive wallets
                var activeWalletIds = activeSwaps.Select(g => g.Key).ToHashSet();
                var inactiveWallets = _activeSwapsByWallet.Keys.Except(activeWalletIds).ToList();
                
                foreach (var walletId in inactiveWallets)
                {
                    await RemoveWebSocketConnection(walletId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error monitoring Boltz swaps");
            }

            // Wait before next check
            await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
        }
    }

    private async Task EnsureWebSocketConnection(string walletId, string[] swapIds)
    {
        if (!_webSocketClients.TryGetValue(walletId, out var client))
        {
            // Create new WebSocket client for this wallet
            var wsUri = new Uri("wss://api.boltz.exchange/v2/ws");
            client = new BoltzWebsocketClient(wsUri);
            client.OnAnyEventReceived += (response) => OnWebSocketEvent(walletId, response);
            
            _webSocketClients[walletId] = client;
            _activeSwapsByWallet[walletId] = new HashSet<string>();
            
            await client.ConnectAsync(_cancellationTokenSource!.Token);
        }

        // Update subscriptions for this wallet
        var currentSwaps = _activeSwapsByWallet[walletId];
        var newSwaps = swapIds.Except(currentSwaps).ToArray();
        var removedSwaps = currentSwaps.Except(swapIds).ToArray();

        if (newSwaps.Length > 0)
        {
            await client.SubscribeAsync(newSwaps, _cancellationTokenSource!.Token);
            foreach (var swapId in newSwaps)
            {
                currentSwaps.Add(swapId);
            }
        }

        if (removedSwaps.Length > 0)
        {
            await client.UnsubscribeAsync(removedSwaps, _cancellationTokenSource!.Token);
            foreach (var swapId in removedSwaps)
            {
                currentSwaps.Remove(swapId);
            }
        }
    }

    private async Task RemoveWebSocketConnection(string walletId)
    {
        if (_webSocketClients.TryRemove(walletId, out var client))
        {
            await client.DisposeAsync();
        }
        _activeSwapsByWallet.TryRemove(walletId, out _);
    }

    private async Task OnWebSocketEvent(string walletId, WebSocketResponse response)
    {
        try
        {
            if (response.Event == "update" && response.Channel == "swap.update" && response.Args?.Count > 0)
            {
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]?.ToString();
                    var status = swapUpdate["status"]?.ToString();
                    
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(status))
                    {
                        var evnt = new BoltzSwapStatusChangedEvent(walletId, id, status);
                        
                        using var scope = serviceProvider.CreateScope();
                        var dbContextFactory = scope.ServiceProvider.GetRequiredService<ArkPluginDbContextFactory>();
                        
                        await HandleReverseSwapUpdate(dbContextFactory, evnt);
                        
                        var eventAggregator = scope.ServiceProvider.GetRequiredService<EventAggregator>();
                        eventAggregator.Publish(new BoltzSwapStatusChangedEvent(walletId, id, status));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing WebSocket event for wallet {WalletId}", walletId);
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
        
        foreach (var client in _webSocketClients.Values)
        {
            // For synchronous dispose, just close without awaiting
            _ = client.DisposeAsync();
        }
        _webSocketClients.Clear();
    }
}

public record BoltzSwapStatusChangedEvent(string WalletId, string SwapId, string Status);
