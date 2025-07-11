
using BTCPayServer.Plugins.ArkPayServer.Services;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;

namespace NArk.Services
{
    /// <summary>
    /// Utility class for building and constructing Ark transactions
    /// </summary>
    public class ArkTransactionBuilder
    {
        private readonly Network _network;

        public ArkTransactionBuilder(Network network)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
        }

        /// <summary>
        /// Constructs an Ark transaction with checkpoint transactions for each input
        /// </summary>
        /// <param name="coins">Collection of coins and their respective signers</param>
        /// <param name="outputs">Output transactions</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The Ark transaction and checkpoint transactions with their input witnesses</returns>
        public async Task<(PSBT arkTx, (PSBT checkpoint, WitScript inputWitness)[])> ConstructArkTransaction(
            ArkCoinWithSigner[] coins,
            TxOut[] outputs,
            CancellationToken cancellationToken)
        {
            var p2a = Script.FromHex("51024e73"); // Standard Ark protocol marker

            List<(PSBT checkpoint, WitScript inputWitness)> checkpoints = new();
            List<(ArkCoin coin, IArkadeWalletSigner signer)> checkpointCoins = new();

            // Create checkpoint transactions for each input coin
            foreach (var coin in coins)
            {
                // Create checkpoint contract
                var checkpointContract = CreateCheckpointContract(coin.Contract);
                
                // Build checkpoint transaction
                var checkpoint = _network.CreateTransactionBuilder();
                checkpoint.SetVersion(3);
                checkpoint.SetFeeWeight(0);
                checkpoint.AddCoin(coin);
                checkpoint.DustPrevention = false;
                checkpoint.Send(p2a, Money.Zero);
                checkpoint.SendAllRemaining(checkpointContract.GetArkAddress());

                var checkpointTx = checkpoint.BuildPSBT(false);
                
                // Sign the checkpoint transaction
                var checkpointGtx = checkpointTx.GetGlobalTransaction();
                var checkpointPrecomputedTransactionData = 
                    checkpointGtx.PrecomputeTransactionData([coin.TxOut]);

                var input = checkpointGtx.Inputs.FindIndexedInput(coin.Outpoint);
                
                // Add Ark PSBT fields
                checkpointTx.Inputs[(int)input.Index].Unknown.SetArkField(
                    coin.Contract.GetTapTree());
                
                // Get signature hash for the input
                var tapLeaf = GetCollaborativePathLeaf(coin.Contract);
                var hash = checkpointGtx.GetSignatureHashTaproot(
                    checkpointPrecomputedTransactionData,
                    new TaprootExecutionData((int)input.Index, tapLeaf.LeafHash));
                
                // Sign and create witness
                var sig = await coin.Signer.Sign(
                    hash, 
                    coin.Contract is TweakedArkPaymentContract tweaked ? tweaked.Tweak : null, 
                    cancellationToken);
                
                var witness = CreateWitness(coin.Contract, sig);
                
                // Add to checkpoints collection
                checkpoints.Add((checkpointTx, witness));
                
                // Create checkpoint coin for the Ark transaction
                var txout = checkpointTx.Outputs.Single(output =>
                    output.ScriptPubKey == checkpointContract.GetArkAddress().ScriptPubKey);
                var outpoint = new OutPoint(checkpointGtx, txout.Index);
                checkpointCoins.Add((
                    new ArkCoin(checkpointContract, outpoint, txout.GetTxOut()!), 
                    coin.Signer));
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
            
            // Sign each input in the Ark transaction
            var gtx = tx.GetGlobalTransaction();
            var precomputedTransactionData =
                gtx.PrecomputeTransactionData(checkpointCoins.Select(x => x.coin.TxOut).ToArray());
                
            foreach (var (coin, signer) in checkpointCoins)
            {
                var contract = (GenericArkContract)coin.Contract;
                var input = gtx.Inputs.FindIndexedInput(coin.Outpoint);
                
                // Add Ark PSBT field
                tx.Inputs[(int)input.Index].Unknown.SetArkField(contract.GetTapTree());
                
                // Get collaborative path and create signature
                var collabPath = contract.GetScriptBuilders().OfType<CollaborativePathArkTapScript>().Single();
                var tapleaf = collabPath.Build();
                var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
                    new TaprootExecutionData((int)input.Index, tapleaf.LeafHash));
                var sig = await signer.Sign(hash, null, cancellationToken);
                
                // Create final script witness
                tx.Inputs[(int)input.Index].FinalScriptWitness = new WitScript(
                    Op.GetPushOp(sig.ToBytes()),
                    Op.GetPushOp(tapleaf.Script.ToBytes()),
                    Op.GetPushOp(contract.GetTaprootSpendInfo().GetControlBlock(tapleaf).ToBytes()));
            }
            
            return (tx, checkpoints.ToArray());
        }

        /// <summary>
        /// Creates a checkpoint contract based on the input contract type
        /// </summary>
        private ArkContract CreateCheckpointContract(ArkContract inputContract)
        {
            switch (inputContract)
            {
                case TweakedArkPaymentContract tweaked:
                    return CreateTweakedCheckpointContract(tweaked);
                    
                case VHTLCContract htlc:
                    return CreateHTLCCheckpointContract(htlc);
                    
                default:
                    throw new NotSupportedException($"Contract type {inputContract.GetType().Name} not supported");
            }
        }

        /// <summary>
        /// Creates a checkpoint contract for a tweaked payment contract
        /// </summary>
        private GenericArkContract CreateTweakedCheckpointContract(TweakedArkPaymentContract contract)
        {
            var scriptBuilders = new List<ScriptBuilder>();
            var delay = contract.GetScriptBuilders().OfType<UnilateralPathArkTapScript>().First().Timeout;
            
            var ownerScript = new NofNMultisigTapScript([contract.User]);
            var serverScript = new NofNMultisigTapScript([contract.Server]);
            
            scriptBuilders.Add(new UnilateralPathArkTapScript(delay, serverScript));
            scriptBuilders.Add(new CollaborativePathArkTapScript(contract.Server, ownerScript));
            
            return new GenericArkContract(contract.Server, scriptBuilders,
                new Dictionary<string, string>()
                {
                    {"server", contract.Server.ToHex()},
                    {"user", contract.OriginalUser.ToHex()},
                    {"tweak", contract.Tweak.ToHex()},
                });
        }

        /// <summary>
        /// Creates a checkpoint contract for an HTLC contract
        /// </summary>
        private GenericArkContract CreateHTLCCheckpointContract(VHTLCContract htlc)
        {
            var scriptBuilders = new List<ScriptBuilder>();
            
            // Determine if this is a claim path or refund path based on the preimage
            if (htlc.Preimage != null)
            {
                // Claim path (receiver with preimage)
                var receiverScript = new NofNMultisigTapScript([htlc.Receiver]);
                var serverScript = new NofNMultisigTapScript([htlc.Server]);
                
                scriptBuilders.Add(new UnilateralPathArkTapScript(htlc.UnilateralClaimDelay, serverScript));
                scriptBuilders.Add(new CollaborativePathArkTapScript(htlc.Server, receiverScript));
                
                return new GenericArkContract(htlc.Server, scriptBuilders,
                    new Dictionary<string, string>()
                    {
                        { "server", htlc.Server.ToHex() },
                        { "receiver", htlc.Receiver.ToHex() },
                        { "sender", htlc.Sender.ToHex() },
                        { "hash", htlc.Hash.ToString() }
                    });
            }
            else
            {
                // Refund path (sender with timelock passed)
                var senderScript = new NofNMultisigTapScript([htlc.Sender]);
                var serverScript = new NofNMultisigTapScript([htlc.Server]);
                
                scriptBuilders.Add(new UnilateralPathArkTapScript(htlc.UnilateralRefundWithoutReceiverDelay, serverScript));
                scriptBuilders.Add(new CollaborativePathArkTapScript(htlc.Server, senderScript));
                
                return new GenericArkContract(htlc.Server, scriptBuilders);
            }
        }

        /// <summary>
        /// Gets the collaborative path leaf for a contract
        /// </summary>
        private TapScript GetCollaborativePathLeaf(ArkContract contract)
        {
            if (contract is ArkPaymentContract arkContract)
            {
                return arkContract.CollaborativePath().Build();
            }
            else if (contract is VHTLCContract htlc)
            {
                return htlc.GetScriptBuilders().OfType<CollaborativePathArkTapScript>().First().Build();
            }
            
            throw new NotSupportedException($"Contract type {contract.GetType().Name} not supported");
        }

        /// <summary>
        /// Creates a witness for a contract
        /// </summary>
        private WitScript CreateWitness(ArkContract contract, SecpSchnorrSignature signature)
        {
            switch (contract)
            {
                case ArkPaymentContract tweaked:
                    return tweaked.CollaborativePathWitness(signature);
                    
                case VHTLCContract {Preimage: not null} htlc:
                    return htlc.ClaimWitness(htlc.Preimage, signature);
                    
                case VHTLCContract htlc:
                    return htlc.RefundWithoutReceiverWitness(signature);
                    
                default:
                    throw new NotSupportedException($"Contract type {contract.GetType().Name} not supported");
            }
        }
    }
    
}
