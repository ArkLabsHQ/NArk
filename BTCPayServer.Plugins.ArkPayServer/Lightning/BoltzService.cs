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
using NArk.Services;
using NArk.Wallet.Boltz;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class BoltzService(
    BTCPayNetworkProvider btcPayNetworkProvider,
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
        // _leases.Add(eventAggregator.SubscribeAsync<BoltzSwapUpdate>(HandleSwapUpdate));
        _leases.Add(eventAggregator.SubscribeAsync<VTXOsUpdated>(OnVTXOs));
        _leases.Add(eventAggregator.SubscribeAsync<LightningSwapUpdated>(OnLightningSwapUpdated));
        _ = ListenForSwapUpdates(cancellationToken);
    }

    private async Task OnLightningSwapUpdated(LightningSwapUpdated arg)
    {
        var active = ArkLightningClient.Map(arg.Swap, btcPayNetworkProvider.BTC.NBitcoinNetwork).Status == LightningInvoiceStatus.Unpaid;
        if (active)
        {
            _activeSwaps.TryAdd(arg.Swap.SwapId, 0);
            if (_wsClient is not null)
            {
               await  _wsClient.SubscribeAsync([arg.Swap.SwapId]);
            }
        }else
        {
            _activeSwaps.TryRemove(arg.Swap.SwapId, out _);
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
            
            await PollActiveManually(cancellationToken);
            try
            {



                var wsurl = boltzClient.DeriveWebSocketUri();
                _wsClient = await BoltzWebsocketClient.CreateAndConnectAsync(wsurl, cancellationToken);
                _wsClient.OnAnyEventReceived += OnWebSocketEvent;
                await _wsClient.SubscribeAsync(_activeSwaps.Keys.ToArray(), cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error  listening for swap updates");
                
                await PollActiveManually(cancellationToken);
                await Task.Delay(5000, cancellationToken);
            }
        }

        return Task.CompletedTask;
    }

    private Task OnWebSocketEvent(WebSocketResponse response)
    {
        try
        {
            if (response.Event == "update" && response is {Channel: "swap.update", Args.Count: > 0})
            {
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]!.GetValue<string>();
                    // var status = swapUpdate["status"]!.GetValue<string>();
                    _ = HandleSwapUpdate(id);
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
            logger.LogError(ex, "Error processing WebSocket event {@response}", response);
        }

        return Task.CompletedTask;
    }
    
    private ConcurrentDictionary<string,int> _activeSwaps = new();

    public async Task PollActiveManually(CancellationToken cancellationToken)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var activeSwaps = dbContext.LightningSwaps.Include(swap => swap.Contract)
            .Where(swap => swap.Contract != null && swap.Contract.Active)
            .ToArrayAsync(cancellationToken);
        var evts = new List<LightningSwapUpdated>();
        foreach (var swap in await activeSwaps)
        {
            var evt = await PollSwapStatus(swap);
            if (evt != null)
                evts.Add(evt);
        }
        _activeSwaps.Clear();
        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (var evt in evts)
        {
            eventAggregator.Publish(evt);
            _activeSwaps.TryAdd(evt.Swap.SwapId, 0);
        }
        
    }
    

    private Task OnVTXOs(VTXOsUpdated arg)
    {
        // TODO
        return Task.CompletedTask;
    }

    private async Task<LightningSwapUpdated?> PollSwapStatus(LightningSwap swap)
    {
       var response = await  boltzClient.GetSwapStatusAsync(swap.SwapId);
       var oldStatus = swap.Status;
       swap.Status = response.Status;
       if (swap is {Status: "invoice.settled", SettledAt: null})
       {
           swap.SettledAt = DateTimeOffset.UtcNow;
       }
       return oldStatus != swap.Status ? new LightningSwapUpdated(swap) : null;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _leases.Dispose();
        _leases = new CompositeDisposable();
        return Task.CompletedTask;
    }
    
    private async Task HandleSwapUpdate(string swapId )
    {
        logger.LogInformation($"Processing swap {swapId} update");

            await using var dbContext = dbContextFactory.CreateContext();

            // Find the swap in the database
            var swaps = await dbContext.LightningSwaps
                .Where(s => s.SwapId == swapId).ToListAsync();

            var evts = new List<LightningSwapUpdated>();
            foreach (var swap in  swaps)
            {
                var evt = await PollSwapStatus(swap);
                if (evt != null)
                    evts.Add(evt);
            }
            await dbContext.SaveChangesAsync();
            foreach (var evt in evts)
            {
                eventAggregator.Publish(evt);
            }

    }


    public async Task<LightningSwap> CreateReverseSwap(string walletId, LightMoney amount, CancellationToken cancellationToken )
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
            // Store the swap in the database with VHTLCContract information
            // First, create and save the ArkWalletContract
            var contractScript = swapResult.VHTLCContract.GetArkAddress().ScriptPubKey.ToHex();
            arkWalletContract =new ArkWalletContract
            {
                Script = contractScript,
                WalletId = walletId,
                Type = swapResult.VHTLCContract.Type,
                Active = true,
                ContractData = swapResult.VHTLCContract.GetContractData()
            };
                return (arkWalletContract, swapResult.VHTLCContract);
        }, cancellationToken);

        if (swapResult is null || contract is not VHTLCContract htlcContract) 
        {
            throw new InvalidOperationException("Failed to create reverse swap");
        }       

        var contractScript = htlcContract.GetArkAddress().ScriptPubKey.ToHex();

        var reverseSwap = new LightningSwap
        {
            SwapId = swapResult.SwapId,
            WalletId = walletId,
            SwapType = "reverse",
            Invoice = swapResult.Invoice,
            LockupAddress = swapResult.LockupAddress,
            OnchainAmount = swapResult.OnchainAmount,
            TimeoutBlockHeight = swapResult.TimeoutBlockHeight,
            PreimageHash = Encoders.Hex.EncodeData(swapResult.PreimageHash),
            ClaimAddress = swapResult.ClaimAddress,
            ContractScript = contractScript, // Reference the contract by script
            Status = "swap.created",
            Contract = arkWalletContract!
        };

        await dbContext.LightningSwaps.AddAsync(reverseSwap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        eventAggregator.Publish(new LightningSwapUpdated(reverseSwap));
        return reverseSwap;
    }
    
}