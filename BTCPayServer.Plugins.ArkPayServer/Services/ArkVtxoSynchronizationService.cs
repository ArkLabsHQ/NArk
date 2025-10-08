using Microsoft.Extensions.Logging;
using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Cache;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using Microsoft.Extensions.Hosting;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
using BTCPayServer.Services;
using NArk;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Payment;
using PayoutData = BTCPayServer.Data.PayoutData;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkVtxoSynchronizationService(
    ILogger<ArkVtxoSynchronizationService> logger,
    AsyncKeyedLocker asyncKeyedLocker,
    EventAggregator eventAggregator,
    TrackedContractsCache contractsCache,
    ArkPluginDbContextFactory arkPluginDbContextFactory,
    IndexerService.IndexerServiceClient indexerClient) : BackgroundService
{
    private CancellationTokenSource? _lastLoopCts = null;
    private Task? _lastListeningLoop = null;
    private string? _subscriptionId = null;
    private readonly TaskCompletionSource _startedTcs = new();
    public Task Started => _startedTcs.Task;
    public bool IsActive => _lastListeningLoop is not null && _lastListeningLoop.Status == TaskStatus.Running;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SubscriptionUpdateLoop(stoppingToken);
    }

    private async Task SubscriptionUpdateLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var waitForCacheUpdate = await eventAggregator.WaitNext<ArkCacheUpdated>(stoppingToken);
            if (waitForCacheUpdate.CacheName is not nameof(TrackedContractsCache)) continue;
            var contracts = contractsCache.Contracts;
            var payouts = contractsCache.Payouts;
            
            var subscribedContractScripts = contracts.Select(c => c.Script).ToHashSet();
            var subscribedPayoutScripts = payouts.Select(GetPayoutScript).ToHashSet();

            var subscribedScripts = subscribedContractScripts.Concat(subscribedPayoutScripts).ToHashSet();
            
            logger.LogInformation(
                "Updating subscription with {ActiveContractsCount} active contracts and {PendingPayoutsCount}.",
                subscribedContractScripts.Count,
                subscribedPayoutScripts.Count
            );

            var req = new SubscribeForScriptsRequest();

            // Only use existing subscriptionId when fake flag is false, true IsFake flag shows that something has gone wrong 
            if (_subscriptionId is not null && !waitForCacheUpdate.IsFake)
                req.SubscriptionId = _subscriptionId;

            req.Scripts.AddRange(subscribedScripts);

            await PollScriptsForVtxos(subscribedScripts, stoppingToken);

            _startedTcs.TrySetResult();

            try
            {
                var subscribeRes = await indexerClient.SubscribeForScriptsAsync(req, cancellationToken: stoppingToken);
                _subscriptionId = subscribeRes.SubscriptionId;
                logger.LogInformation("Successfully subscribed with ID: {SubscriptionId}", _subscriptionId);
                StartListening(subscribeRes.SubscriptionId, stoppingToken);
            }
            catch (RpcException ex)
            {
                logger.LogError(ex, "Failed to subscribe to scripts. Republishing the event with Fake flag.");
                eventAggregator.Publish(waitForCacheUpdate with { IsFake = true });
            }

        }
    }

    private string GetPayoutScript(PayoutData payout)
    {
        return ArkAddress.Parse(payout.DedupId!).ScriptPubKey.ToHex();
    }

    private void StartListening(string subscriptionId, CancellationToken stoppingToken)
    {
        if (_lastListeningLoop is { IsCompleted: false })
        {
            logger.LogDebug("Listener already running.");
            return;
        }

        _lastLoopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _lastListeningLoop = ListenToStream(subscriptionId, _lastLoopCts.Token);
        logger.LogInformation("Stream listener started.");
    }

    private async Task ListenToStream(string subscriptionId, CancellationToken token)
    {
        try
        {
            logger.LogInformation("Connecting to stream with subscription ID: {SubscriptionId}", subscriptionId);
            var stream = indexerClient.GetSubscription(new GetSubscriptionRequest { SubscriptionId = subscriptionId }, cancellationToken: token);

            await foreach (var response in stream.ResponseStream.ReadAllAsync(token))
            {
                if (response == null) continue;
                logger.LogDebug("Received update for {Count} scripts.", response.Scripts.Count);

                await PollScriptsForVtxos(response.Scripts.ToHashSet(), token);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            logger.LogInformation("Stream was cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stream listener failed. It will be restarted on the next check.");
            // The main loop will handle restarting the subscription.
            // To ensure it restarts, we can trigger a check.
            eventAggregator.Publish(new ArkCacheUpdated(nameof(TrackedContractsCache), true));
        }
        finally
        {
            logger.LogInformation("ListenToStream finished.");
        }
    }
    
    public async Task PollScriptsForVtxos(IReadOnlySet<string> allScripts, CancellationToken cancellationToken)
    {
        if (allScripts.Count == 0)
            return;

        using var l = await asyncKeyedLocker.LockAsync($"script-sync-lock", cancellationToken);

        // Query scripts in 1000 script chunks
        foreach (var scripts in allScripts.Chunk(1000))
        {
            var request = new GetVtxosRequest()
            {
                Scripts = { scripts },
                RecoverableOnly = false,
                SpendableOnly = false,
                SpentOnly = false,
                Page = new IndexerPageRequest()
                {
                    Index = 0,
                    Size = 1000
                }
            };

            await using var dbContext = arkPluginDbContextFactory.CreateContext();

            var existingVtxos =
                await dbContext
                    .Vtxos
                    .Where(x => scripts.Contains(x.Script))
                    .ToListAsync(cancellationToken: cancellationToken);

            var vtxosUpdated = new List<VTXO>();

            GetVtxosResponse? response = null;

            while (response is null || response.Page.Next != response.Page.Total)
            {
                response = await indexerClient.GetVtxosAsync(request, cancellationToken: cancellationToken);

                var vtxosToProcess = new Queue<IndexerVtxo>(response.Vtxos);

                while (vtxosToProcess.TryDequeue(out var vtxoToProccess))
                {
                    if (existingVtxos.Find(v =>
                            v.TransactionId == vtxoToProccess.Outpoint.Txid &&
                            v.TransactionOutputIndex == vtxoToProccess.Outpoint.Vout) is { } existing)
                    {
                        Map(vtxoToProccess, existing);
                        if (dbContext.Entry(existing).State == EntityState.Modified)
                        {
                            vtxosUpdated.Add(existing);
                        }
                    }
                    else
                    {
                        var newVtxo = Map(vtxoToProccess);
                        await dbContext.Vtxos.AddAsync(newVtxo, cancellationToken);
                        vtxosUpdated.Add(newVtxo);
                    }
                }

                request.Page.Index = response.Page.Next;
            }


            await dbContext.SaveChangesAsync(cancellationToken);
            if (vtxosUpdated.Count != 0)
            {
                var updateEvent = new VTXOsUpdated([.. vtxosUpdated]);
                logger.LogInformation("Publishing event: {Event}", updateEvent.ToString());
                eventAggregator.Publish(updateEvent);
            }
        }
    }

    public static VTXO Map(IndexerVtxo vtxo, VTXO? existing = null)
    {
        existing ??= new VTXO();

        existing.TransactionId = vtxo.Outpoint.Txid;
        existing.TransactionOutputIndex = (int)vtxo.Outpoint.Vout;
        existing.Amount = (long)vtxo.Amount;
        existing.IsNote = vtxo.IsSwept;
        existing.SeenAt = DateTimeOffset.FromUnixTimeSeconds(vtxo.CreatedAt);
        existing.SpentByTransactionId = string.IsNullOrEmpty(vtxo.SpentBy) ? null : vtxo.SpentBy;
        existing.Script = vtxo.Script;

        return existing;
    }
    
    private record struct VtxoIdentifier(string TxId, uint Vout);


}