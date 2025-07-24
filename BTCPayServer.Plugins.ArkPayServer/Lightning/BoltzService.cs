using System.Collections.Concurrent;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Lightning.Events;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Boltz.Client;
using NArk.Boltz.Models.WebSocket;
using NArk.Contracts;
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
    ILogger<BoltzService> logger) : IHostedService
{
    private CompositeDisposable _leases = new();
    private BoltzWebsocketClient? _wsClient;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _leases.Add(eventAggregator.SubscribeAsync<ArkSwapUpdated>(OnLightningSwapUpdated));
        _ = ListenForSwapUpdates(cancellationToken);
    }

    private async Task OnLightningSwapUpdated(ArkSwapUpdated arg)
    {
        var active = arg.Swap.Status == ArkSwapStatus.Pending;
        if (active)
        {
            if (_activeSwaps.TryAdd(arg.Swap.SwapId, 0))
            {
                logger.LogInformation("Subscribed to swap {SwapId}", arg.Swap.SwapId);
            }
            if (_wsClient is not null)
            {
               await  _wsClient.SubscribeAsync([arg.Swap.SwapId]);
            }
        }else
        {
            if(_activeSwaps.TryRemove(arg.Swap.SwapId, out _))
            {
                logger.LogInformation("Unsubscribed to swap {SwapId}", arg.Swap.SwapId);
            }
            if (_wsClient is not null)
            {
                await  _wsClient.UnsubscribeAsync([arg.Swap.SwapId]);
            }
        }
        
    }

    private async Task<object> ListenForSwapUpdates(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            
            try
            {

                await PollActiveManually(null, cancellationToken);


                var wsurl = boltzClient.DeriveWebSocketUri();
                _wsClient = await BoltzWebsocketClient.CreateAndConnectAsync(wsurl, cancellationToken);
                _wsClient.OnAnyEventReceived += OnWebSocketEvent;
                await _wsClient.SubscribeAsync(_activeSwaps.Keys.ToArray(), cancellationToken);
                await _wsClient.WaitUntilDisconnected(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error  listening for swap updates");
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
    
    private readonly ConcurrentDictionary<string,int> _activeSwaps = new();

    public async Task PollActiveManually(Func<IQueryable<ArkSwap>, IQueryable<ArkSwap>>? query = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = dbContextFactory.CreateContext();
            var queryable =  dbContext.Swaps.Include(swap => swap.Contract)
                .Where(swap => swap.Status == ArkSwapStatus.Pending);

            if (query is not null)
                queryable = query(queryable);
            
            var activeSwaps =await queryable.ToArrayAsync(cancellationToken);
        
            if(!activeSwaps.Any())
                return;
            var scripts = activeSwaps.Select(swap => swap.ContractScript).ToHashSet();
            var vtxos = await dbContext.Vtxos.Where(vtxo => scripts.Contains(vtxo.Script))
                .GroupBy(vtxo => vtxo.Script).ToDictionaryAsync(group => group.Key, group => group.ToArray(), cancellationToken);
            var evts = new List<ArkSwapUpdated>();
            foreach (var swap in activeSwaps)
            {
                var evt = await PollSwapStatus(swap);
                if (evt != null)
                {
                    evt.Vtxos = vtxos.TryGet(swap.ContractScript);
                    evts.Add(evt);
                }
                
            }
            _activeSwaps.Clear();
            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (var evt in evts)
            {
                eventAggregator.Publish(evt);
                _activeSwaps.TryAdd(evt.Swap.SwapId, 0);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error polling active swaps");
        }
    }

    private async Task<ArkSwapUpdated?> PollSwapStatus(ArkSwap swap)
    {
       var response = await  boltzClient.GetSwapStatusAsync(swap.SwapId);
       var oldStatus = swap.Status;
       if (Map(response.Status) is var newStatus && newStatus != oldStatus)
       {
           swap.UpdatedAt = DateTimeOffset.UtcNow;
           swap.Status = newStatus;
           return new ArkSwapUpdated(swap);  
       }
       return null;
    }

    public ArkSwapStatus Map(string status)
    {
        return status switch
        {
            "swap.created" => ArkSwapStatus.Pending,
            "invoice.expired" => ArkSwapStatus.Failed,
            "swap.expired" => ArkSwapStatus.Failed,
            "transaction.failed" => ArkSwapStatus.Failed,
            "transaction.refunded" => ArkSwapStatus.Failed,
            "transaction.mempool" => ArkSwapStatus.Pending,
            "transaction.confirmed" => ArkSwapStatus.Settled,
            "invoice.settled" => ArkSwapStatus.Settled,
            _ => ArkSwapStatus.Pending
        };
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _leases.Dispose();
        _leases = new CompositeDisposable();
        return Task.CompletedTask;
    }
    
    private async Task HandleSwapUpdate(string swapId )
    {
        await PollActiveManually(swaps => swaps.Where(swap => swap.SwapId == swapId), CancellationToken.None);
    }


    public async Task<ArkSwap> CreateReverseSwap(string walletId, LightMoney amount, CancellationToken cancellationToken )
    {
        var signer = await walletService.CanHandle(walletId, cancellationToken);
        if (!signer)
        {
             throw new InvalidOperationException("No signer found for wallet");
        }

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
            var receiverKey = wallet.PublicKey;

            // Create reverse swap with just the receiver key - sender key comes from Boltz response
            swapResult = await boltzSwapService.CreateReverseSwap(
                amount.MilliSatoshi/1000, 
                receiverKey,
                cancellationToken: cancellationToken);
            
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
        eventAggregator.Publish(new ArkSwapUpdated(reverseSwap));
        return reverseSwap;
    }
    public async Task<ArkSwap> CreateSubmarineSwap(string walletId, BOLT11PaymentRequest paymentRequest, CancellationToken cancellationToken )
    {
        var signer = await walletService.CanHandle(walletId, cancellationToken);
        if (!signer)
        {
             throw new InvalidOperationException("No signer found for wallet");
        }

        await using var dbContext = dbContextFactory.CreateContext();
        
        // Get the wallet from the database to extract the receiver key
        var wallet = await dbContext.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
        if (wallet == null)
        {
            throw new InvalidOperationException($"Wallet with ID {walletId} not found");
        }
        

        SubmarineSwapResult swapResult = null;
        ArkWalletContract? arkWalletContract = null;
        var contract = await walletService.DeriveNewContract(walletId, async wallet =>
        {
            var sender = wallet.PublicKey;

            // Create reverse swap with just the receiver key - sender key comes from Boltz response
            swapResult = await boltzSwapService.CreateSubmarineSwap(
                paymentRequest,
                sender,
                cancellationToken: cancellationToken);
            
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
            Hash = new uint256(Hashes.SHA256(htlcContract.Preimage)).ToString()
        };
        await dbContext.Swaps.AddAsync(submarineSwap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        eventAggregator.Publish(new ArkSwapUpdated(submarineSwap));
        
        await arkadeSpender.Spend(walletId, [ new TxOut(Money.Satoshis(submarineSwap.ExpectedAmount), htlcContract.GetArkAddress())], cancellationToken);
        
        return submarineSwap;
    }
    
}