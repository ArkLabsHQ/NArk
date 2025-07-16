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
    EventAggregator eventAggregator,
    ArkPluginDbContextFactory dbContextFactory,
    BoltzSwapService boltzSwapService,
    BoltzClient boltzClient,
    ArkWalletService walletService,
    ILogger<BoltzService> logger) : IHostedService
{
    private CompositeDisposable _leases = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _leases.Add(eventAggregator.SubscribeAsync<BoltzSwapUpdate>(HandleSwapUpdate));
        _leases.Add(eventAggregator.SubscribeAsync<VTXOsUpdated>(OnVTXOs));

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
        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (var evt in evts)
        {
            eventAggregator.Publish(evt);
        }
    }

    private Task OnVTXOs(VTXOsUpdated arg)
    {
        throw new NotImplementedException();
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
    
    private async Task HandleSwapUpdate(BoltzSwapUpdate e)
    {
        logger.LogInformation($"Processing reverse swap {e.SwapId} update");

            await using var dbContext = dbContextFactory.CreateContext();

            // Find the swap in the database
            var swaps = await dbContext.LightningSwaps
                .Where(s => s.SwapId == e.SwapId).ToListAsync();

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
            
            return (new ArkWalletContract
            {
                Script = contractScript,
                WalletId = walletId,
                Type = swapResult.VHTLCContract.Type,
                Active = true,
                ContractData = swapResult.VHTLCContract.GetContractData()
            }, swapResult.VHTLCContract);
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
            Status = "swap.created"
        };

        await dbContext.LightningSwaps.AddAsync(reverseSwap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        eventAggregator.Publish(new LightningSwapUpdated(reverseSwap));
        return reverseSwap;
    }
    
}