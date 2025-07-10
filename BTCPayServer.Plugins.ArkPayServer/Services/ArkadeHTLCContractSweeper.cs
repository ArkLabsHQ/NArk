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

public class ArkadeHTLCContractSweeper : IHostedService
{
    private readonly ArkService.ArkServiceClient _arkServiceClient;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly ArkadeWalletSignerProvider _walletSignerProvider;
    private readonly EventAggregator _eventAggregator;
    private readonly IOperatorTermsService _operatorTermsService;
    private readonly ILogger<ArkadeHTLCContractSweeper> _logger;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private CompositeDisposable leases = new();
    CancellationTokenSource cts = new();

    public ArkadeHTLCContractSweeper(
        ArkService.ArkServiceClient arkServiceClient,
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        ArkadeWalletSignerProvider walletSignerProvider,
        EventAggregator eventAggregator,
        IOperatorTermsService operatorTermsService,
        ILogger<ArkadeHTLCContractSweeper> logger,
        BTCPayNetworkProvider btcPayNetworkProvider)
    {
        _arkServiceClient = arkServiceClient;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _walletSignerProvider = walletSignerProvider;
        _eventAggregator = eventAggregator;
        _operatorTermsService = operatorTermsService;
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
                    db.WalletContracts.Where(c => c.Type == VHTLCContract.ContractType), // Only tweaked contracts
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

                var wallet = await db.Wallets.FirstOrDefaultAsync(x => x.Id == group.Key, cts.Token);
                if (wallet is null)
                    continue;

                var publicKey = wallet.PublicKey;

                // lets find htlcs with vtxos that we can sweep
                // for example, if we are the receiver + we have the preimage, we can sweep it
                // if we are the sender + we have the preimage + timelock has passed, we can sweep it
                // the other path is cooperatively signed, so we'd need a way to get a sig from boltz

                var toSweepWithClaimPath = new List<ArkCoin>();
                var toSweepWithRefundPath = new List<ArkCoin>();

                foreach (var vtxo in group)
                {
                    var arkCoin = ToArkCoin(vtxo.Contract, vtxo.Vtxo);
                    var htlc = (VHTLCContract)arkCoin.Contract;
                    if (htlc.Receiver == publicKey && htlc.Preimage is not null)
                    {
                        toSweepWithClaimPath.Add(arkCoin);
                    }
                    else if (htlc.Sender == publicKey && htlc.RefundLocktime.IsTimeLock &&
                             htlc.RefundLocktime.Date < DateTime.UtcNow)
                    {
                        toSweepWithRefundPath.Add(arkCoin);
                    }
                }

                if (toSweepWithClaimPath.Count > 0 || toSweepWithRefundPath.Count > 0)
                {
                    var total = Money.Satoshis(toSweepWithClaimPath.Concat(toSweepWithRefundPath)
                        .Sum(x => x.TxOut.Value));

                    var operatorTerms = await _operatorTermsService.GetOperatorTerms(cts.Token);
                    var destination = new ArkPaymentContract(operatorTerms.SignerKey,
                        operatorTerms.UnilateralExit, publicKey);

                    var txout = new TxOut(total, destination.GetArkAddress());
                    var arkTx = await ConstructArkTransaction(signer, toSweepWithClaimPath, toSweepWithRefundPath,
                        [txout], cts.Token);

                    var submitRequest = new SubmitTxRequest();
                    submitRequest.SignedArkTx = arkTx.arkTx.ToBase64();
                    submitRequest.CheckpointTxs.AddRange(arkTx.checkpoints.Select(x => x.checkpoint.ToBase64()).ToArray());
                    var response = await _arkServiceClient.SubmitTxAsync(submitRequest);
                    var parsedReceivedCheckpoints = response.SignedCheckpointTxs.Select(x => PSBT.Parse(x, _network))
                        .ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());
                    var signedCheckpoints =
                        arkTx.checkpoints.ToDictionary(psbt => psbt.checkpoint.GetGlobalTransaction().GetHash());
                    foreach (var signedCheckpoint in signedCheckpoints)
                    {
                        var serverSig = parsedReceivedCheckpoints[signedCheckpoint.Key].Inputs[0].FinalScriptWitness
                            .Pushes
                            .First();

                        signedCheckpoint.Value.checkpoint.Inputs[0].FinalScriptWitness = new WitScript(
                            signedCheckpoint.Value.inputWitness.Pushes.Concat([serverSig]).ToArray());
                    }

                    FinalizeTxRequest finalizeTxRequest = new();
                    finalizeTxRequest.ArkTxid = response.ArkTxid;
                    finalizeTxRequest.FinalCheckpointTxs.AddRange(signedCheckpoints
                        .Select(x => x.Value.checkpoint.ToBase64()).ToArray());
                    var finalizeTxResponse = await _arkServiceClient.FinalizeTxAsync(finalizeTxRequest);
                    // _eventAggregator.Publish(new VTXOsUpdated(finalizeTxResponse.Vtxos));
                }
            }

            using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await tcsWaitForNextPoll.Task.WithCancellation(CancellationTokenSource
                .CreateLinkedTokenSource(cts.Token, cts2.Token).Token);
        }
    }

    private async Task<(PSBT arkTx, (PSBT checkpoint, WitScript inputWitness)[] checkpoints)> ConstructArkTransaction(
        IArkadeWalletSigner signer, 
        List<ArkCoin> toSweepWithClaimPath,
        List<ArkCoin> toSweepWithRefundPath, 
        TxOut[] outputs,
        CancellationToken cancellationToken)
    {
        var p2a = Script.FromHex("51024e73");

        List<(PSBT checkpoint, WitScript inputWitness)> checkpoints = new();
        List<(ArkCoin coin, IArkadeWalletSigner signer)> checkpointCoins = new();

        // Process claim path coins (receiver with preimage)
        foreach (var coin in toSweepWithClaimPath)
        {
            var htlc = (VHTLCContract)coin.Contract;
            
            // Create checkpoint contract for claim path
            var checkpointScriptBuilders = new List<ScriptBuilder>();
            var receiverScript = new NofNMultisigTapScript([htlc.Receiver]);
            var serverScript = new NofNMultisigTapScript([htlc.Server]);
            
            checkpointScriptBuilders.Add(new UnilateralPathArkTapScript(htlc.UnilateralClaimDelay, serverScript));
            checkpointScriptBuilders.Add(new CollaborativePathArkTapScript(htlc.Server, receiverScript));
            
            var checkpointContract = new GenericArkContract(htlc.Server, checkpointScriptBuilders,
                new Dictionary<string, string>()
                {
                    { "server", htlc.Server.ToHex() },
                    { "receiver", htlc.Receiver.ToHex() },
                    { "sender", htlc.Sender.ToHex() },
                    { "hash", htlc.Hash.ToString() }
                });
            
            // Build checkpoint transaction
            var checkpoint = _network.CreateTransactionBuilder();
            checkpoint.SetVersion(3);
            checkpoint.SetFeeWeight(0);
            checkpoint.AddCoin(coin);
            checkpoint.DustPrevention = false;
            checkpoint.Send(p2a, Money.Zero);
            checkpoint.SendAllRemaining(checkpointContract.GetArkAddress());

            var checkpointTx = checkpoint.BuildPSBT(false);
            
            // Sign the checkpoint transaction using claim path
            var checkpointgtx = checkpointTx.GetGlobalTransaction();
            var checkpointPrecomputedTransactionData =
                checkpointgtx.PrecomputeTransactionData([coin.TxOut]);

            var input = checkpointgtx.Inputs.FindIndexedInput(coin.Outpoint);
            var tapLeaf = htlc.GetScriptBuilders().OfType<CollaborativePathArkTapScript>().First().Build();
            var hash = checkpointgtx.GetSignatureHashTaproot(checkpointPrecomputedTransactionData,
                new TaprootExecutionData((int)input.Index, tapLeaf.LeafHash));
            
            var sig = await signer.Sign(hash, null, cancellationToken);
            var witness = htlc.ClaimWitness(htlc.Preimage!, sig);
            
            checkpoints.Add((checkpointTx, witness));
            var txout = checkpointTx.Outputs.Single(output =>
                output.ScriptPubKey == checkpointContract.GetArkAddress().ScriptPubKey);
            var outpoint = new OutPoint(checkpointgtx, txout.Index);
            checkpointCoins.Add((new ArkCoin(checkpointContract, outpoint, txout.GetTxOut()!), signer));
        }
        
        // Process refund path coins (sender with timelock passed)
        foreach (var coin in toSweepWithRefundPath)
        {
            var htlc = (VHTLCContract)coin.Contract;
            
            // Create checkpoint contract for refund path
            var checkpointScriptBuilders = new List<ScriptBuilder>();
            var senderScript = new NofNMultisigTapScript([htlc.Sender]);
            var serverScript = new NofNMultisigTapScript([htlc.Server]);
            
            checkpointScriptBuilders.Add(new UnilateralPathArkTapScript(htlc.UnilateralRefundWithoutReceiverDelay, serverScript));
            checkpointScriptBuilders.Add(new CollaborativePathArkTapScript(htlc.Server, senderScript));
            
            var checkpointContract = new GenericArkContract(htlc.Server, checkpointScriptBuilders);
            
            // Build checkpoint transaction
            var checkpoint = _network.CreateTransactionBuilder();
            checkpoint.SetVersion(3);
            checkpoint.SetFeeWeight(0);
            checkpoint.AddCoin(coin);
            checkpoint.DustPrevention = false;
            checkpoint.Send(p2a, Money.Zero);
            checkpoint.SendAllRemaining(checkpointContract.GetArkAddress());

            var checkpointTx = checkpoint.BuildPSBT(false);
            
            // Sign the checkpoint transaction using refund path
            var checkpointgtx = checkpointTx.GetGlobalTransaction();
            var checkpointPrecomputedTransactionData =
                checkpointgtx.PrecomputeTransactionData([coin.TxOut]);

            var input = checkpointgtx.Inputs.FindIndexedInput(coin.Outpoint);
            var tapLeaf = htlc.GetScriptBuilders().OfType<CollaborativePathArkTapScript>().First().Build();
            var hash = checkpointgtx.GetSignatureHashTaproot(checkpointPrecomputedTransactionData,
                new TaprootExecutionData((int)input.Index, tapLeaf.LeafHash));
            
            var sig = await signer.Sign(hash, null, cancellationToken);
            var witness = htlc.RefundWithoutReceiverWitness(sig);
            
            checkpoints.Add((checkpointTx, witness));
            var txout = checkpointTx.Outputs.Single(output =>
                output.ScriptPubKey == checkpointContract.GetArkAddress().ScriptPubKey);
            var outpoint = new OutPoint(checkpointgtx, txout.Index);
            checkpointCoins.Add((new ArkCoin(checkpointContract, outpoint, txout.GetTxOut()!), signer));
        }

        // Build the Ark transaction that spends from all checkpoint outputs
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
        var precomputedTransactionData =
            gtx.PrecomputeTransactionData(checkpointCoins.Select(x => x.coin.TxOut).ToArray());
            
        // Sign each input in the Ark transaction
        foreach (var coin in checkpointCoins)
        {
            var contract = (GenericArkContract)coin.coin.Contract;
            var input = gtx.Inputs.FindIndexedInput(coin.coin.Outpoint);
            var collabPath = contract.GetScriptBuilders().OfType<CollaborativePathArkTapScript>().Single();
            var tapleaf = collabPath.Build();
            var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
                new TaprootExecutionData((int)input.Index, tapleaf.LeafHash));
            var sig = await coin.signer.Sign(hash, null, cancellationToken);
            tx.Inputs[(int)input.Index].FinalScriptWitness = new WitScript(
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