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
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Services;


public class ArkadeWalletSignerProvider
{
    private readonly IEnumerable<IArkadeMultiWalletSigner> _walletSigners;

    ArkadeWalletSignerProvider(IEnumerable<IArkadeMultiWalletSigner> walletSigners)
    {
        _walletSigners = walletSigners;
    }

    public async Task<IArkadeWalletSigner> GetSigner(string walletId, CancellationToken cancellationToken = default)
    {
        var signers = await GetSigners([walletId],cancellationToken);
        if (signers.TryGetValue(walletId, out var signer))
        {
            return signer;
        }
        throw new Exception($"Could not find a signer for wallet {walletId}");
    }

    public async Task<Dictionary<string, IArkadeWalletSigner>> GetSigners(string[] walletId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IArkadeWalletSigner>();
        foreach (var signer in _walletSigners)
        {
            foreach (var id in walletId)
            {
                if (await signer.CanHandle(id, cancellationToken))
                {
                    result.Add(id, await signer.CreateSigner(id, cancellationToken));
                }
            }
        }
        return result;
        
    }

}

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

    private static ArkCoin ToArkCoin(ArkWalletContract c, VTXO vtxo)
    {
        var cobtract = ArkContract.Parse(c.Type, c.ContractData);
        var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);
        var txout = new TxOut(Money.Satoshis(vtxo.Amount), cobtract.GetArkAddress());
        return new ArkCoin(cobtract, outpoint, txout);
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
                    .Select(x => ToArkCoin(x.Contract, x.Vtxo)).ToArray();
                var total = Money.Satoshis(arkCoins.Sum(x => x.TxOut.Value));

                var contract = ArkContract.Parse(group.First().Contract.Type, group.First().Contract.ContractData);
       
                var txout = new TxOut(total, contract.GetArkAddress());
                var arkTx = await ConstructArkTransaction(arkCoins.Select(x => (signer, x)).ToArray(), [txout], cts.Token);
                
                var submitRequest = new SubmitTxRequest();
                submitRequest.SignedArkTx = arkTx.arkTx.ToBase64();
                submitRequest.CheckpointTxs.AddRange(arkTx.Item2.Select(x => x.checkpoint.ToBase64()).ToArray());
                var response = await _arkServiceClient.SubmitTxAsync(submitRequest);
                var parsedReceivedCheckpoints = response.SignedCheckpointTxs.Select(x => PSBT.Parse(x, _network)).ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());
                var signedCheckpoints =
                    arkTx.Item2.ToDictionary(psbt => psbt.checkpoint.GetGlobalTransaction().GetHash());
                foreach (var signedCheckpoint in signedCheckpoints)
                {
                    var serverSig = parsedReceivedCheckpoints[signedCheckpoint.Key].Inputs[0].FinalScriptWitness.Pushes
                        .First();

                    signedCheckpoint.Value.checkpoint.Inputs[0].FinalScriptWitness = new WitScript(
                        signedCheckpoint.Value.inputWitness.Pushes.Concat([serverSig]).ToArray());
                }

                FinalizeTxRequest finalizeTxRequest = new();
                finalizeTxRequest.ArkTxid = response.ArkTxid;
                finalizeTxRequest.FinalCheckpointTxs.AddRange(signedCheckpoints.Select(x => x.Value.checkpoint.ToBase64()).ToArray());
                var finalizeTxResponse = await _arkServiceClient.FinalizeTxAsync(finalizeTxRequest);
                // _eventAggregator.Publish(new VTXOsUpdated(finalizeTxResponse.Vtxos));
            }
            
            
            using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await tcsWaitForNextPoll.Task.WithCancellation( CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cts2.Token).Token);
        }
    }
    
    private async Task<(PSBT arkTx, (PSBT checkpoint, WitScript inputWitness)[])> ConstructArkTransaction((IArkadeWalletSigner signer, 
        ArkCoin coin)[]coins, 
        TxOut[] outputs,
        CancellationToken cancellationToken)
    {
        
        var p2a = Script.FromHex("51024e73");

        List<(PSBT checkpoint, WitScript inputWitness)> checkpoints = new();
        List<(ArkCoin coin ,IArkadeWalletSigner signer)> checkpointCoins = new();
        
        
        foreach (var coin in coins)
        {
            var cloned = (TweakedArkPaymentContract) ArkContract.Parse(coin.coin.Contract.ToString());
            var scriptBuilders = cloned.GetScriptBuilders().ToList();
            var checkpointScriptBuilders = new List<ScriptBuilder>();

            var delay = scriptBuilders.OfType<UnilateralPathArkTapScript>().First().Timeout;
            
            var ownerScript = new NofNMultisigTapScript( [cloned.User]);
            var serverScript = new NofNMultisigTapScript( [cloned.Server]);
            checkpointScriptBuilders.Add(new UnilateralPathArkTapScript(delay, serverScript));
            checkpointScriptBuilders.Add(new CollaborativePathArkTapScript(cloned.Server, ownerScript));
            
            var checkpointContract = new GenericArkContract(cloned.Server, checkpointScriptBuilders,
                new Dictionary<string, string>()
                {
                    {"server", cloned.Server.ToHex()},
                    {"user", cloned.OriginalUser.ToHex()},
                    {"tweak", cloned.Tweak.ToHex()},
                });
            
            var checkpoint = _network.CreateTransactionBuilder();
            checkpoint.SetVersion(3);
            checkpoint.SetFeeWeight(0);
            checkpoint.AddCoin(coin.coin);
            checkpoint.DustPrevention = false;
            checkpoint.Send(p2a, Money.Zero);
            checkpoint.SendAllRemaining(checkpointContract.GetArkAddress());

            
            var checkpointTx = checkpoint.BuildPSBT(false);
            
            
            var contract = (TweakedArkPaymentContract)coin.coin.Contract;
            var checkpointgtx = checkpointTx.GetGlobalTransaction();
            var checkpointPrecomputedTransactionData = checkpointgtx.PrecomputeTransactionData(checkpointCoins.Select(x => x.coin.TxOut).ToArray());

            var input = checkpointgtx.Inputs.FindIndexedInput(coin.coin.Outpoint);
            var tapleaf = contract.CollaborativePath().Build().LeafHash;
            var hash = checkpointgtx.GetSignatureHashTaproot(checkpointPrecomputedTransactionData,
                new TaprootExecutionData((int)input.Index, tapleaf));
            var sig = await coin.signer.Sign(hash,contract.Tweak, cancellationToken );
            var witness = contract.CollaborativePathWitness(sig);
            
            checkpoints.Add((checkpointTx, witness));
            var txout = checkpointTx.Outputs.Single(output =>
                output.ScriptPubKey == checkpointContract.GetArkAddress().ScriptPubKey);
            var outpoint = new OutPoint(checkpointgtx, txout.Index);
            checkpointCoins.Add((new ArkCoin(checkpointContract, outpoint, txout.GetTxOut()!), coin.signer));
        }
        
        var arkTx = _network.CreateTransactionBuilder();
        arkTx.SetVersion(3);
        arkTx.SetFeeWeight(0);
        arkTx.DustPrevention = false;
        
        arkTx.Send(p2a, Money.Zero);
        foreach (var coin in checkpointCoins)
        {
            arkTx.AddCoin(coin.coin);
        }
        foreach (var output in outputs)
        {
            arkTx.Send(output.ScriptPubKey, output.Value);
        }
        
        var tx = arkTx.BuildPSBT(false);
        
        var gtx = tx.GetGlobalTransaction();
        var precomputedTransactionData = gtx.PrecomputeTransactionData(checkpointCoins.Select(x => x.coin.TxOut).ToArray());
        foreach (var coin in checkpointCoins)
        {
            var contract = (GenericArkContract)coin.coin.Contract;
            var input = gtx.Inputs.FindIndexedInput(coin.coin.Outpoint);
            var collabPath  = contract.GetScriptBuilders().OfType<CollaborativePathArkTapScript>().Single();
            var tapleaf = collabPath.Build();
            var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
                new TaprootExecutionData((int)input.Index, tapleaf.LeafHash));
            var sig = await coin.signer.Sign(hash,null, cancellationToken );
            tx.Inputs[(int)input.Index].FinalScriptWitness =  new WitScript(
                Op.GetPushOp(sig.ToBytes()), 
                Op.GetPushOp(tapleaf.Script.ToBytes()),
                Op.GetPushOp(contract.GetTaprootSpendInfo().GetControlBlock(tapleaf).ToBytes()));
        }
        
        return (tx, checkpoints.ToArray());
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