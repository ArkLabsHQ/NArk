using System.Threading.Channels;
using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Services.Invoices;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkSubscriptionService : IHostedService, IAsyncDisposable
{
    private readonly AsyncKeyedLocker _asyncKeyedLocker;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly IndexerService.IndexerServiceClient _indexerClient;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<ArkSubscriptionService> _logger;

    private Task _processingTask;
    private CancellationTokenSource? _cts;
    private readonly Channel<bool> _checkContractsChannel = Channel.CreateUnbounded<bool>();

    private string _subscriptionId = "";
    private HashSet<string> _subscribedScripts = new();
    private Task? _listeningTask;
    private CancellationTokenSource _listeningCts;
    private readonly Network _network;

    public ArkSubscriptionService(
        BTCPayNetworkProvider btcPayNetworkProvider,
        AsyncKeyedLocker asyncKeyedLocker,
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        IndexerService.IndexerServiceClient indexerClient,
        EventAggregator eventAggregator,
        ILogger<ArkSubscriptionService> logger)
    {
        _asyncKeyedLocker = asyncKeyedLocker;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _indexerClient = indexerClient;
        _eventAggregator = eventAggregator;
        _logger = logger;
        _network = btcPayNetworkProvider.BTC.NBitcoinNetwork;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = ProcessingLoop(_cts.Token);
        _logger.LogInformation("ArkSubscriptionService started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ArkSubscriptionService stopping.");
        if (_processingTask == null)
            return;

        _checkContractsChannel.Writer.TryComplete();
        if(_cts is not null)
            await _cts.CancelAsync();

        await Task.WhenAny(_processingTask, Task.Delay(Timeout.Infinite, cancellationToken));
        _logger.LogInformation("ArkSubscriptionService stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            await StopListening();
            if (!_cts.IsCancellationRequested)
                await _cts.CancelAsync();
            _cts.Dispose();
        }
    }

    public void TriggerContractsCheck()
    {
        _checkContractsChannel.Writer.TryWrite(true);
    }

    public async Task UpdateManualSubscriptionAsync(string contract, bool subscribe, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(contract))
        {
            _logger.LogWarning("Attempted to manually {Action} a null or empty contract.",
                subscribe ? "subscribe to" : "unsubscribe from");
            return;
        }

        using var keyLocker = await _asyncKeyedLocker.LockAsync("UpdateSubscription", cancellationToken);

        if (subscribe)
        {
            if (_subscribedScripts.Add(contract))
            {
                _logger.LogInformation("Manually subscribing to contract: {Contract}", contract);
                await SynchronizeSubscriptionWithIndexerAsync(cancellationToken);
                await ProcessUpdates([contract], cancellationToken);
            }
            else
            {
                _logger.LogInformation("Contract {Contract} is already in the manual subscription list.", contract);
            }
        }
        else
        {
            if (_subscribedScripts.Remove(contract))
            {
                _logger.LogInformation("Manually unsubscribing from contract: {Contract}", contract);
                await SynchronizeSubscriptionWithIndexerAsync(cancellationToken);
            }
            else
            {
                _logger.LogInformation("Contract {Contract} was not in the manual subscription list.", contract);
            }
        }
    }

    private async Task ProcessingLoop(CancellationToken cancellationToken)
    {
        TriggerContractsCheck(); // Initial check

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _checkContractsChannel.Reader.ReadAsync(cancellationToken);
                await UpdateSubscriptionAndListen(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ArkSubscriptionService processing loop. Retrying in 1 minute.");
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
    }

    

    private async Task UpdateSubscriptionAndListen(CancellationToken cancellationToken)
    {
        using var keyLocker = await _asyncKeyedLocker.LockAsync("UpdateSubscription", cancellationToken);

        await using var dbContext = _arkPluginDbContextFactory.CreateContext();
        var activeContracts = await dbContext.WalletContracts
            .Where(c => c.Active)
            .Select(c => c.Script)
            .ToListAsync(cancellationToken);

        var activeScripts = new HashSet<string>(activeContracts);

        if (activeScripts.SetEquals(_subscribedScripts))
        {
            _logger.LogDebug("No change in active contracts, skipping subscription update.");
            // Still check if listener is running
            if ((_listeningTask is null || _listeningTask.IsCompleted) && activeScripts.Any())
            {
                _logger.LogInformation("Listener was not running, but there are active contracts. Starting listener.");
                await StartListening(cancellationToken);
            }
            return;
        }

        _subscribedScripts = activeScripts;

        if (_subscribedScripts.Count == 0)
        {
            _logger.LogInformation("No active contracts. Stopping listener.");
            await StopListening();
            _subscriptionId = "";
            return;
        }

        _logger.LogInformation("Updating subscription with {Count} active contracts.", _subscribedScripts.Count);

        var req = new SubscribeForScriptsRequest { SubscriptionId = _subscriptionId };
        req.Scripts.AddRange(_subscribedScripts);

        try
        {
            var subscribeRes = await _indexerClient.SubscribeForScriptsAsync(req, cancellationToken: cancellationToken);
            _subscriptionId = subscribeRes.SubscriptionId;
            _logger.LogInformation("Successfully subscribed with ID: {SubscriptionId}", _subscriptionId);

            await StartListening(cancellationToken);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to subscribe to scripts. Will retry on next check.");
            _subscribedScripts.Clear(); // Force retry on next trigger
        }
    }

    private async Task StartListening(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_subscriptionId))
        {
            _logger.LogWarning("Cannot start listening without a subscription ID.");
            return;
        }
        if (_listeningTask is { IsCompleted: false })
        {
            _logger.LogDebug("Listener already running.");
            return;
        }

        await StopListening(); // Ensure previous listener is stopped

        _listeningCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listeningTask = ListenToStream(_subscriptionId, _listeningCts.Token);
        _logger.LogInformation("Stream listener started.");
    }

    private async Task StopListening()
    {
        if (_listeningTask == null) return;

        _logger.LogInformation("Stopping stream listener.");
        if(_listeningCts is not null && !_listeningCts.IsCancellationRequested)
            _listeningCts.Cancel();

        await Task.WhenAny(_listeningTask, Task.Delay(Timeout.Infinite, CancellationToken.None));

        if(_listeningCts is not null)
            _listeningCts.Dispose();
        _listeningTask = null;
        _listeningCts = null;
        _logger.LogInformation("Stream listener stopped.");
    }

    private async Task ListenToStream(string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Connecting to stream with subscription ID: {SubscriptionId}", subscriptionId);
            var stream = _indexerClient.GetSubscription(new GetSubscriptionRequest { SubscriptionId = subscriptionId }, cancellationToken: cancellationToken);

            await foreach (var response in stream.ResponseStream.ReadAllAsync(cancellationToken))
            {
                if (response == null) continue;
                _logger.LogDebug("Received update for {Count} scripts.", response.Scripts.Count);
                await ProcessUpdates(response.Scripts.ToArray(), cancellationToken);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Stream was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream listener failed. It will be restarted on the next check.");
            // The main loop will handle restarting the subscription.
            // To ensure it restarts, we can trigger a check.
            TriggerContractsCheck();
        }
        finally
        {
            _logger.LogInformation("ListenToStream finished.");
        }
    }

    public static VTXO Map(IndexerVtxo vtxo)
    {
        return new VTXO()
        {
            TransactionId = vtxo.Outpoint.Txid,
            TransactionOutputIndex = (int)vtxo.Outpoint.Vout,
            Amount = (long)vtxo.Amount,
            IsNote = vtxo.IsSwept,
            SeenAt = DateTimeOffset.FromUnixTimeSeconds(vtxo.CreatedAt),
            SpentByTransactionId = vtxo.SpentBy,
            Script = vtxo.Script
        };
        
    }

    private async Task ProcessUpdates(string[] scripts, CancellationToken cancellationToken)
    {
        // var handler = _arkadePaymentMethodHandler;
        var request = new GetVtxosRequest();
        request.Scripts.AddRange(scripts);
        var vtxos = await _indexerClient.GetVtxosAsync(request, cancellationToken: cancellationToken);

        await using var dbContext = _arkPluginDbContextFactory.CreateContext();

       var results = await  dbContext.Vtxos.UpsertRange(vtxos.Vtxos.Select(Map)).RunAndReturnAsync();
       _eventAggregator.Publish(new VTXOsUpdated()
       {
           Vtxos = results.ToArray()
       });
    }
    

    private async Task SynchronizeSubscriptionWithIndexerAsync(CancellationToken cancellationToken)
    {
        if (_subscribedScripts.Count == 0)
        {
            _logger.LogInformation("[Manual] No active scripts. Stopping listener and clearing subscription.");
            await StopListening();
            _subscriptionId = "";
            return;
        }

        _logger.LogInformation("[Manual] Updating remote subscription with {Count} scripts.", _subscribedScripts.Count);

        var req = _subscriptionId is null ?
            new SubscribeForScriptsRequest() :
            new SubscribeForScriptsRequest { SubscriptionId = _subscriptionId };
        req.Scripts.AddRange(_subscribedScripts);

        try
        {
            var subscribeRes = await _indexerClient.SubscribeForScriptsAsync(req, cancellationToken: cancellationToken);
            var newSubscriptionId = subscribeRes.SubscriptionId;

            if (_subscriptionId != newSubscriptionId && !string.IsNullOrEmpty(_subscriptionId))
            {
                _logger.LogWarning("Subscription ID changed from {OldSubscriptionId} to {NewSubscriptionId} during manual update. Listener will be restarted.", _subscriptionId, newSubscriptionId);
                await StopListening();
            }
            _subscriptionId = newSubscriptionId;
            _logger.LogInformation("[Manual] Successfully updated subscription with ID: {SubscriptionId}", _subscriptionId);

            await StartListening(cancellationToken);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "[Manual] Failed to update remote subscription.");
        }
    }

}