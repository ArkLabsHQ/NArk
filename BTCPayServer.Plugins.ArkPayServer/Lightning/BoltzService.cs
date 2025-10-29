using System.Collections.Concurrent;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Lightning.Events;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Boltz.Client;
using NArk.Boltz.Models.WebSocket;
using NArk.Contracts;
using NArk.Models;
using NArk.Services;
using NBitcoin;
using NBitcoin.Crypto;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class BoltzService(
    ArkadeSpender arkadeSpender,
    EventAggregator eventAggregator,
    ArkPluginDbContextFactory dbContextFactory,
    BoltzSwapService boltzSwapService,
    BoltzClient boltzClient,
    ArkWalletService walletService,
    ArkVtxoSynchronizationService arkVtxoSynchronizationService,
    ILogger<BoltzService> logger) : IHostedService
{
    private CompositeDisposable _leases = new();
    private BoltzWebsocketClient? _wsClient;
    private CancellationTokenSource? _periodicPollCts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _leases.Add(eventAggregator.SubscribeAsync<ArkSwapUpdated>(OnLightningSwapUpdated));
        _leases.Add(eventAggregator.SubscribeAsync<VTXOsUpdated>(VTXOSUpdated));
        
        _periodicPollCts = new CancellationTokenSource();
        _ = ListenForSwapUpdates(_periodicPollCts.Token);
        _ = PeriodicSwapPolling(_periodicPollCts.Token);
        
        return Task.CompletedTask;
    }

    private async Task VTXOSUpdated(VTXOsUpdated arg)
    {
       var scripts = arg.Vtxos.Select(vtxo => vtxo.Script).ToArray();
       await PollActiveManually(swaps => swaps.Where(swap => scripts.Contains(swap.ContractScript)), CancellationToken.None);
    }

    private async Task OnLightningSwapUpdated(ArkSwapUpdated arg)
    {
        if (arg.Swap.Status.IsActive())
        {
            if (_activeSwaps.TryAdd(arg.Swap.SwapId, arg.Swap.ContractScript))
            {
                logger.LogInformation("Subscribed to swap {SwapId}", arg.Swap.SwapId);
            }
            if (_wsClient is not null)
            {
               await  _wsClient.SubscribeAsync([arg.Swap.SwapId]);
            }
        }
        else
        {
            if(_activeSwaps.TryRemove(arg.Swap.SwapId, out _))
            {
                logger.LogInformation("Unsubscribed to swap {SwapId}", arg.Swap.SwapId);
            }
            if (_wsClient is not null)
            {
                await  _wsClient.UnsubscribeAsync([arg.Swap.SwapId]);
            }
            
            // Trigger contract sync when swap completes to detect VTXOs
            _ = Task.Run(async () =>
            {
                try
                {
                    logger.LogInformation("Triggering contract sync for swap {SwapId} with script {Script}", 
                        arg.Swap.SwapId, arg.Swap.ContractScript);
                    await arkVtxoSynchronizationService.PollScriptsForVtxos(new HashSet<string>([arg.Swap.ContractScript]), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error syncing contract after swap {SwapId} status update", arg.Swap.SwapId);
                }
            });
        }
        
    }

    private async Task<object> ListenForSwapUpdates(CancellationToken cancellationToken)
    {
        var error = "";
        while (!cancellationToken.IsCancellationRequested)
        {
            Uri? wsUrl = null;
            try
            {
                if(error == "")
                    logger.LogInformation("Start listening for swap updates.");
                wsUrl = boltzClient.DeriveWebSocketUri();
                _wsClient = await BoltzWebsocketClient.CreateAndConnectAsync(wsUrl, cancellationToken);
                error = "";
                logger.LogInformation("Listening for swap updates at {wsUrl}", wsUrl);
                _wsClient.OnAnyEventReceived += OnWebSocketEvent;
                await _wsClient.SubscribeAsync(_activeSwaps.Keys.ToArray(), cancellationToken);
                
                await PollActiveManually(null, cancellationToken);
                await _wsClient.WaitUntilDisconnected(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                var newError = $"Error  listening for swap updates at {wsUrl}";
                if (error != newError)
                {
                    error = newError;
                    logger.LogError(e, error); ;
                }

                try
                {
                    await PollActiveManually(null, cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Error polling active swaps as failsafe");
                }
                await Task.Delay(5000, cancellationToken);
            }
        }

        return Task.CompletedTask;
    }

    private Task OnWebSocketEvent(WebSocketResponse? response)
    {
        try
        {
            if (response is null)
                return Task.CompletedTask;
            
            if (response.Event == "update" && response is {Channel: "swap.update", Args.Count: > 0})
            {
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]!.GetValue<string>();
                    _ = HandleSwapUpdate(id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing WebSocket event {@response}", response);
        }

        return Task.CompletedTask;
    }
    
    private readonly ConcurrentDictionary<string,string> _activeSwaps = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    public IReadOnlyDictionary<string, string> GetActiveSwapsCache() => _activeSwaps;

    public async Task<(List<ArkSwapUpdated> Updates, HashSet<string> MatchedScripts)> PollActiveManually(Func<IQueryable<ArkSwap>, IQueryable<ArkSwap>>? query = null, CancellationToken cancellationToken = default)
    {
        await _pollLock.WaitAsync(cancellationToken);
        
        try
        {
            await using var dbContext = dbContextFactory.CreateContext();
            var queryable = dbContext.Swaps
                .Include(swap => swap.Contract)
                .Where(swap => swap.Status == ArkSwapStatus.Pending || swap.Status == ArkSwapStatus.Unknown);

            if (query is not null)
                queryable = query(queryable);
            
            var activeSwaps = await queryable.ToArrayAsync(cancellationToken);
            if (activeSwaps.Length == 0)
            {
                return ([], []);
            }
            
            // Collect all matched contract scripts
            var matchedScripts = activeSwaps.Select(s => s.ContractScript).ToHashSet();
            
            var evts = new List<ArkSwapUpdated>();
            foreach (var swap in activeSwaps)
            {
                var evt = await PollSwapStatus(swap);
                if (evt != null)
                {
                    evts.Add(evt);
                }
                
            }
            
            await dbContext.SaveChangesAsync(cancellationToken);
            
            // Update cache: add active swaps, remove inactive ones
            foreach (var evt in evts)
            {
                if (evt.Swap.Status.IsActive())
                {
                    _activeSwaps.TryAdd(evt.Swap.SwapId, evt.Swap.ContractScript);
                }
                else
                {
                    _activeSwaps.TryRemove(evt.Swap.SwapId, out _);
                }
            }
            PublishUpdates(evts.ToArray());
            return (evts, matchedScripts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error polling active swaps");
        }
        finally
        {
            _pollLock.Release();
        }

        return ([], []);
    }

    private void PublishUpdates(params ArkSwapUpdated[] updates)
    {
        var sb = new StringBuilder();
        foreach (var update in updates)
        {
            sb.AppendLine(update.ToString());
            eventAggregator.Publish(update);
        }
        logger.LogInformation(sb.ToString());
    }

    private async Task<ArkSwapUpdated?> PollSwapStatus(ArkSwap swap)
    {
        try
        {
            var response = await boltzClient.GetSwapStatusAsync(swap.SwapId);
            var oldStatus = swap.Status;
            if (Map(response.Status) is var newStatus && newStatus != oldStatus)
            {
                swap.UpdatedAt = DateTimeOffset.UtcNow;
                swap.Status = newStatus;
                return new ArkSwapUpdated { Swap = swap };
            }
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Swap not found on Boltz - mark as unknown
            logger.LogWarning("Swap {SwapId} not found on Boltz server", swap.SwapId);
            var oldStatus = swap.Status;
            if (oldStatus != ArkSwapStatus.Unknown)
            {
                swap.UpdatedAt = DateTimeOffset.UtcNow;
                swap.Status = ArkSwapStatus.Unknown;
                return new ArkSwapUpdated { Swap = swap };
            }
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error polling swap status for {SwapId}", swap.SwapId);
            return null;
        }
    }

    public ArkSwapStatus Map(string status)
    {
        switch (status)
        {
            case "swap.created":
                return ArkSwapStatus.Pending;
            case "invoice.expired":
            case "swap.expired":
            case "transaction.failed":
            case "transaction.refunded":
                return ArkSwapStatus.Failed;
            case "transaction.mempool":
                return ArkSwapStatus.Pending;
            case "transaction.confirmed":
            case "invoice.settled":
                return ArkSwapStatus.Settled;
            default:
                logger.LogInformation("Unknown status {Status}", status);
                return ArkSwapStatus.Unknown;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _periodicPollCts?.Cancel();
        _periodicPollCts?.Dispose();
        _leases.Dispose();
        _leases = new CompositeDisposable();
        _pollLock.Dispose();
        return Task.CompletedTask;
    }

    private async Task HandleSwapUpdate(string swapId)
    {
        logger.LogInformation("Received swap update for {SwapId}", swapId);
        var (updates, matchedScripts) =
            await PollActiveManually(swaps => swaps.Where(swap => swap.SwapId == swapId), CancellationToken.None);

        // Always sync VTXOs when we receive a WebSocket update, even if status didn't change
        // The swap may have progressed (e.g., invoice paid, funds received) without changing our status mapping
        if (matchedScripts.Count > 0)
        {
            if (updates.Count == 0)
            {
                logger.LogInformation("No status change for swap {SwapId}, but syncing {Count} contract(s) anyway", 
                    swapId, matchedScripts.Count);
            }
            
            await arkVtxoSynchronizationService.PollScriptsForVtxos(matchedScripts, CancellationToken.None);
        }
    }

    /// <summary>
    /// Periodic polling failsafe to ensure no swaps are missed due to WebSocket issues or race conditions.
    /// Polls all pending swaps every 5 minutes.
    /// </summary>
    private async Task PeriodicSwapPolling(CancellationToken cancellationToken)
    {
        // Wait 30 seconds before starting periodic polling to allow initial setup
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        
        logger.LogInformation("Starting periodic swap polling failsafe (every 5 minutes)");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                
                logger.LogDebug("Running periodic swap poll failsafe");
                var (updates, matchedScripts) = await PollActiveManually(null, cancellationToken);
                
                if (updates.Count > 0)
                {
                    logger.LogInformation("Periodic poll detected {Count} swap status changes", updates.Count);
                    
                    // Sync VTXOs for any completed swaps
                    var completedScripts = updates
                        .Where(u => !u.Swap.Status.IsActive())
                        .Select(u => u.Swap.ContractScript)
                        .ToHashSet();
                    
                    if (completedScripts.Count > 0)
                    {
                        await arkVtxoSynchronizationService.PollScriptsForVtxos(completedScripts, cancellationToken);
                    }
                }
                else if (matchedScripts.Count > 0)
                {
                    // No status changes but we have pending swaps - sync them anyway
                    logger.LogDebug("Periodic poll: no status changes, but syncing {Count} pending swap contract(s)", matchedScripts.Count);
                    await arkVtxoSynchronizationService.PollScriptsForVtxos(matchedScripts, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in periodic swap polling failsafe");
                // Continue polling despite errors
            }
        }
        
        logger.LogInformation("Periodic swap polling stopped");
    }

    public async Task<ArkSwap> CreateReverseSwap(string walletId, CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellationToken)
    {
        if (!await walletService.CanHandle(walletId, cancellationToken))
        {
             throw new InvalidOperationException("No signer found for wallet");
        }

        var signer =await  walletService.CreateSigner(walletId, cancellationToken);

        await using var dbContext = dbContextFactory.CreateContext();
        
        // Get the wallet from the database to extract the receiver key
        var wallet = await dbContext.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
        if (wallet == null)
        {
            throw new InvalidOperationException($"Wallet with ID {walletId} not found");
        }
        
        

        ReverseSwapResult? swapResult = null;
        ArkWalletContract? arkWalletContract = null;
        var contract = await walletService.DeriveNewContract(walletId, async wallet =>
        {
            var receiverKey = await signer.GetPublicKey(cancellationToken);

            // Create reverse swap with just the receiver key - sender key comes from Boltz response
            swapResult = await boltzSwapService.CreateReverseSwap(
                createInvoiceRequest,
                receiverKey, cancellationToken);
            
            var contractScript = swapResult.Contract.GetArkAddress().ScriptPubKey.ToHex();
            arkWalletContract =new ArkWalletContract
            {
                Script = contractScript,
                WalletId = walletId,
                Type = swapResult.Contract.Type,
                Active = true,
                ContractData = swapResult.Contract.GetContractData()
            };
                return (arkWalletContract, swapResult.Contract);
        }, cancellationToken);

        if (swapResult is null || arkWalletContract is null || contract is not VHTLCContract htlcContract) 
        {
            throw new InvalidOperationException("Failed to create reverse swap");
        }       

        var contractScript = htlcContract.GetArkAddress().ScriptPubKey.ToHex(); 
        dbContext.ChangeTracker.TrackGraph(arkWalletContract, node => node.Entry.State = EntityState.Unchanged);

        var reverseSwap = new ArkSwap
        {
            SwapId = swapResult.Swap.Id,
            WalletId = walletId,
            SwapType =  ArkSwapType.ReverseSubmarine,
            Invoice = swapResult.Swap.Invoice,
            ExpectedAmount = swapResult.Swap.OnchainAmount,
            ContractScript = contractScript,
            Contract = arkWalletContract!,
            Status = ArkSwapStatus.Pending,
            Hash = new uint256(swapResult.Hash).ToString()
        };
        await dbContext.Swaps.AddAsync(reverseSwap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishUpdates(new ArkSwapUpdated { Swap = reverseSwap });
        return reverseSwap;
    }
    public async Task<ArkSwap> CreateSubmarineSwap(string walletId, BOLT11PaymentRequest paymentRequest, CancellationToken cancellationToken )
    {
        if (!await walletService.CanHandle(walletId, cancellationToken))
        {
            throw new InvalidOperationException("No signer found for wallet");
        }
        
        var signer = await walletService.CreateSigner(walletId, cancellationToken);

        await using var dbContext = dbContextFactory.CreateContext();
        
        var swap = await dbContext.Swaps.FirstOrDefaultAsync(s => s.Invoice == paymentRequest.ToString(), cancellationToken);
        if (swap != null)
        {
            return swap;
        }
        
        
        SubmarineSwapResult? swapResult = null;
        ArkWalletContract? arkWalletContract = null;
        var contract = await walletService.DeriveNewContract(walletId, async wallet =>
        {
            var sender = await signer.GetPublicKey(cancellationToken);

            // Create reverse swap with just the receiver key - sender key comes from Boltz response
            swapResult = await boltzSwapService.CreateSubmarineSwap(
                paymentRequest,
                sender,
                cancellationToken: cancellationToken);
            
            var contractScript = swapResult.Contract.GetArkAddress().ScriptPubKey.ToHex();
            arkWalletContract = new ArkWalletContract
            {
                Script = contractScript,
                WalletId = walletId,
                Type = swapResult.Contract.Type,
                Active = true,
                ContractData = swapResult.Contract.GetContractData()
            };

            return (arkWalletContract, swapResult.Contract);
        }, cancellationToken);

        if (swapResult is null || arkWalletContract is null || contract is not VHTLCContract htlcContract) 
        {
            throw new InvalidOperationException("Failed to create reverse swap");
        }       

        var contractScript = htlcContract.GetArkAddress().ScriptPubKey.ToHex(); 
        dbContext.ChangeTracker.TrackGraph(arkWalletContract, node => node.Entry.State = EntityState.Unchanged);
        var submarineSwap = new ArkSwap
        {
            SwapId = swapResult.Swap.Id,
            WalletId = walletId,
            SwapType =  ArkSwapType.Submarine,
            Invoice = paymentRequest.ToString(),
            ExpectedAmount = swapResult.Swap.ExpectedAmount,
            ContractScript = contractScript,
            Contract = arkWalletContract!,
            Status = ArkSwapStatus.Pending,
            Hash = paymentRequest.Hash.ToString()
        };
        await dbContext.Swaps.AddAsync(submarineSwap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        PublishUpdates(new ArkSwapUpdated { Swap = submarineSwap });
        
        await arkadeSpender.Spend(walletId, [ new TxOut(Money.Satoshis(submarineSwap.ExpectedAmount), htlcContract.GetArkAddress())], cancellationToken);
        
        return submarineSwap;
    }
    
}