//
// using BTCPayServer.Data;
// using BTCPayServer.Plugins.ArkPayServer.Data;
// using BTCPayServer.Services.Stores;
// using Microsoft.Extensions.Hosting;
// using Microsoft.Extensions.Logging;
// using NBitcoin;
// using NBitcoin.Secp256k1;
// using NBXplorer;
//
// namespace BTCPayServer.Plugins.ArkPayServer.Services;
//
//

using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Services;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeTweakedContractSweeper:IHostedService
{
    private readonly StoreRepository _storeRepository;
    private readonly ArkService.ArkServiceClient _arkServiceClient;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly ArkadeWalletSignerProvider _walletSignerProvider;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<ArkadeTweakedContractSweeper> _logger;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private CompositeDisposable leases = new();
    CancellationTokenSource cts = new();
    public ArkadeTweakedContractSweeper(
        
        StoreRepository storeRepository, 
        ArkService.ArkServiceClient arkServiceClient,
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        ArkadeWalletSignerProvider walletSignerProvider,
        EventAggregator eventAggregator,
        ILogger<ArkadeTweakedContractSweeper> logger,
        
        BTCPayNetworkProvider btcPayNetworkProvider )
    {
        _storeRepository = storeRepository;
        _arkServiceClient = arkServiceClient;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _walletSignerProvider = walletSignerProvider;
        _eventAggregator = eventAggregator;
        _logger = logger;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _network = _btcPayNetworkProvider.BTC.NBitcoinNetwork;
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        leases.Add(_eventAggregator.SubscribeAsync<VTXOsUpdated>(OnVTXOsUpdated));
        _ = PollForVTXOToSweep();
        return Task.CompletedTask;
    }

    private static ArkCoin ToArkCoin(ArkWalletContract c, VTXO vtxo, IArkadeWalletSigner? signer)
    {
        var cobtract = ArkContract.Parse(c.Type, c.ContractData);
        var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);
        var txout = new TxOut(Money.Satoshis(vtxo.Amount), cobtract.GetArkAddress());
        return signer != null ? new ArkCoinWithSigner(signer, cobtract, outpoint, txout) : new ArkCoin(cobtract, outpoint, txout);
    }

    TaskCompletionSource? tcsWaitForNextPoll = null;
    private readonly Network _network;

    private async Task PollForVTXOToSweep()
    {
        while (!cts.IsCancellationRequested)
        {
            tcsWaitForNextPoll = new TaskCompletionSource();

            await using var db = _arkPluginDbContextFactory.CreateContext();
            
            var vtxosAndContracts = await db.Vtxos
                .Where(vtxo => vtxo.SpentByTransactionId == null && !vtxo.IsNote) // VTXO is unspent
                .Join(
                    db.WalletContracts.Where(c => c.Type == TweakedArkPaymentContract.ContractType), // Only tweaked contracts
                    vtxo => vtxo.Script, // Key from VTXO
                    contract => contract.Script, // Key from WalletContract
                    (vtxo, contract) => new { Vtxo = vtxo, Contract = contract } // Select both VTXO and contract
                )
                .ToListAsync(cts.Token);

            var groupedByWallet = vtxosAndContracts.GroupBy(x => x.Contract.WalletId).ToList();

            var walletsToCheck = groupedByWallet.Select(g => g.Key);
            
            var walletSigners = await _walletSignerProvider.GetSigners(walletsToCheck.ToArray(), cts.Token);
            
            foreach (var group in groupedByWallet)
            {
                var signer = walletSigners.TryGet(group.Key);
                if (signer is null)
                    continue;

                var arkCoins = group
                    .Select(x => ToArkCoin(x.Contract, x.Vtxo,signer)).ToArray();
                var total = Money.Satoshis(arkCoins.Sum(x => x.TxOut.Value));

                var contract = (TweakedArkPaymentContract) ArkContract.Parse(group.First().Contract.Type, group.First().Contract.ContractData);
                var destination = new ArkPaymentContract(contract.Server, contract.ExitDelay, contract.OriginalUser);
                var txout = new TxOut(total, destination.GetArkAddress());
                
                // Use the new ArkTransactionExtensions to create the Ark transaction
                var arkTx = await _network.CreateArkTransaction(
                    (ArkCoinWithSigner[]) arkCoins,
                    [txout],
                    cts.Token);
                
                // Submit the transaction using the extension method
                var finalizeTxResponse = await _arkServiceClient.SubmitArkTransaction(
                    arkTx.arkTx,
                    arkTx.Item2,
                    _network,
                    cts.Token);
                
                // _eventAggregator.Publish(new VTXOsUpdated(finalizeTxResponse.Vtxos));
            }
            
            
            using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await tcsWaitForNextPoll.Task.WithCancellation( CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cts2.Token).Token);
        }
    }
    
    private Task OnVTXOsUpdated(VTXOsUpdated arg)
    {
        tcsWaitForNextPoll?.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await cts.CancelAsync();
        leases.Dispose();
        leases = new CompositeDisposable();
    }
}