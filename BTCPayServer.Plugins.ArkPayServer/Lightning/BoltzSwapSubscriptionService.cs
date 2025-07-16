using System.Collections.Concurrent;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Lightning.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Wallet.Boltz;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Hosted service that continuously monitors Boltz swaps and fires events when status changes occur.
/// This replaces the polling mechanism and provides a centralized way to track swap status.
/// </summary>
public class BoltzSwapSubscriptionService : IHostedService, IDisposable
{
    private BoltzWebsocketClient? _webSocketClient;
    private readonly ConcurrentDictionary<string, byte> _activeSwaps = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly EventAggregator _eventAggregator;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly BoltzClient _boltzClient;
    private readonly ILogger<BoltzSwapSubscriptionService> _logger;
    private readonly CompositeDisposable _leases = new();
    
    readonly SemaphoreSlim _lock = new(1);

    /// <summary>
    /// Hosted service that continuously monitors Boltz swaps and fires events when status changes occur.
    /// This replaces the polling mechanism and provides a centralized way to track swap status.
    /// </summary>
    public BoltzSwapSubscriptionService(
        EventAggregator eventAggregator,
        BTCPayNetworkProvider btcPayNetworkProvider,
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        BoltzClient boltzClient, ILogger<BoltzSwapSubscriptionService> logger)
    {
        _eventAggregator = eventAggregator;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _boltzClient = boltzClient;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Boltz swap monitoring service");
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        _leases.Add(_eventAggregator.SubscribeAsync<LightningSwapUpdated>(OnLightningSwapUpdated));
        _ = InitiateMonitoring();
        return Task.CompletedTask;
    }

    private async Task OnLightningSwapUpdated(LightningSwapUpdated arg)
    {
        await _lock.WaitAsync();
        try
        {
            var active = ArkLightningClient.Map(arg.Swap, _btcPayNetworkProvider.BTC.NBitcoinNetwork).Status == LightningInvoiceStatus.Unpaid;
            var swapId = arg.Swap.SwapId;
            if(active)
            {
                _activeSwaps.TryAdd(swapId, 0);
                await _webSocketClient!.SubscribeAsync([swapId]);
            }
            else
            {
                _activeSwaps.TryRemove(swapId, out _);
                await _webSocketClient!.UnsubscribeAsync([swapId]);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Boltz swap monitoring service");

        if (_cancellationTokenSource is not null)
        {
            await _cancellationTokenSource.CancelAsync();
        }

        if (_webSocketClient is not null)
        {
            await _webSocketClient.DisposeAsync();
        }
        _leases.Dispose();
    }

    private async Task InitiateMonitoring()
    {
        if (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = _arkPluginDbContextFactory.CreateContext();
                
                // Get all active swaps that need monitoring
                var activeSwaps = await dbContext.LightningSwaps
                    .Where(s => s.Status == "created" || s.Status == "pending")
                    .ToListAsync(_cancellationTokenSource.Token);

                await MonitorSwaps(activeSwaps.Select(s => s.SwapId).ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring Boltz swaps");
            }
        }
    }

    public async Task MonitorSwaps(params string[] swapIds)
    {
        await _lock.WaitAsync();
        try
        {
            if (_webSocketClient == null)
            {
                var wsUri =  _boltzClient.DeriveWebSocketUri();
                _webSocketClient = await BoltzWebsocketClient.CreateAndConnectAsync(wsUri, _cancellationTokenSource?.Token ?? CancellationToken.None);
                _webSocketClient.OnAnyEventReceived += OnWebSocketEvent;
            }
        
            foreach (var swapId in swapIds)
            {
                _activeSwaps.TryAdd(swapId, 0);
            }
            await _webSocketClient.SubscribeAsync(swapIds, _cancellationTokenSource!.Token);
        }
        finally
        {
            _lock.Release();
        }
        
    }

    private async Task OnWebSocketEvent(WebSocketResponse response)
    {
        try
        {
            if (response.Event == "update" && response is {Channel: "swap.update", Args.Count: > 0})
            {
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]!.GetValue<string>();
                    var status = swapUpdate["status"]!.GetValue<string>();
                    _eventAggregator.Publish(new BoltzSwapUpdate(id, status));
                    // if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(status))
                    // {
                    //     var inactive = status == "invoice.paid" || status == "invoice.expired" || status == "invoice.canceled";
                    //     
                    //     
                    //     
                    //    var swap = await HandleReverseSwapUpdate(id, status);
                    //     
                    //     var evnt = new BoltzSwapStatusChangedEvent(id, status, !inactive, swap?.ContractScript, swap?.WalletId);
                    //     _eventAggregator.Publish(evnt);
                    // }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WebSocket event {@response}", response);
        }
    }
    
    private async Task<LightningSwap?> HandleReverseSwapUpdate(string swapId, string status)
    {
        
        _logger.LogInformation("Processing reverse swap {SwapId} status update to: {Status}", swapId, status);
        
        try
        {
            await using var dbContext = _arkPluginDbContextFactory.CreateContext();
            
            // Find the swap in the database
            var swap = await dbContext.LightningSwaps
                .FirstOrDefaultAsync(s => s.SwapId == swapId && s.SwapType == "reverse");
                
            if (swap == null)
            {
                _logger.LogWarning("Reverse swap {SwapId} not found in database", swapId);
                return null;
            }
            
            // Update the swap status
            var oldStatus = swap.Status;
            swap.Status = status;
            
            // Set settlement time if swap is being marked as paid
            if (status == "invoice.paid" && swap.SettledAt == null)
            {
                swap.SettledAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("Reverse swap {SwapId} marked as settled", swapId);
            }
            
            await dbContext.SaveChangesAsync();
            
            _logger.LogInformation("Updated reverse swap {SwapId} status from {OldStatus} to {NewStatus}", 
                swapId, oldStatus, status);
                
            if (status == "invoice.paid")
            {
                _logger.LogInformation("Reverse swap {SwapId} paid - VTXO creation should be triggered", swapId);
            }

            return swap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process reverse swap {SwapId} status update", swapId);
            throw;
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        if (_webSocketClient is not null)
        {
            _webSocketClient.DisposeAsync().GetAwaiter().GetResult();
        }
    }
}
