using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.BIP370;

namespace NArk.Services
{
    /// <summary>
    /// Utility class for building and constructing Ark transactions
    /// </summary>
    public class ArkTransactionBuilder
    {
        
        private readonly Network _network;
        private readonly ILogger<ArkTransactionBuilder> _logger;

        public ArkTransactionBuilder(Network network, ILogger<ArkTransactionBuilder> logger)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _logger = logger;
        }

        public async Task<PSBT> FinalizeCheckpointTx(PSBT checkpointTx, PSBT receivedCheckpointTx, ArkCoinWithSigner coin,  CancellationToken cancellationToken)
        {
            _logger.LogDebug("Finalizing checkpoint transaction for coin with outpoint {Outpoint}", coin.Outpoint);
            
            var serverSigKv = receivedCheckpointTx.Inputs[0].Unknown
                .First(pair => pair.Key[0] == PSBTExtraConstants.PSBT_IN_TAP_SCRIPT_SIG);
            
            var serverSig = PSBTExtraConstants.GetTaprootScriptSpendSignature(serverSigKv.Key, serverSigKv.Value);
            
            // Sign the checkpoint transaction
            var checkpointGtx = checkpointTx.GetGlobalTransaction();
            var checkpointPrecomputedTransactionData = 
                checkpointGtx.PrecomputeTransactionData([coin.TxOut]);
            var input = checkpointTx.Inputs.FindIndexedInput(coin.Outpoint);
            
            // var input = checkpointGtx.Inputs.FindIndexedInput(coin.Outpoint);
            var hash = checkpointGtx.GetSignatureHashTaproot(
                checkpointPrecomputedTransactionData,
                new TaprootExecutionData((int)input.Index, serverSig.leafHash));
            //
            // // Sign and create witness
            // _logger.LogDebug("Signing checkpoint transaction for input {InputIndex}", input.Index);
            var sig = await coin.Signer.Sign(
                hash, 
                coin.Contract is TweakedArkPaymentContract tweaked ? tweaked.Tweak : null, 
                cancellationToken);
            
           var (leaf, condition, locktime) =  GetCollaborativePathLeaf(coin.Contract);

           var witness = new List<Op>();
           witness.Add(Op.GetPushOp(serverSig.signature.ToBytes()));
           witness.Add(Op.GetPushOp(sig.ToBytes()));
           if(condition != null)
           {
               witness.AddRange(condition.Pushes.Select(Op.GetPushOp));
           }
           witness.Add(Op.GetPushOp(leaf.Script.ToBytes()));
           witness.Add(Op.GetPushOp(coin.Contract.GetTaprootSpendInfo().GetControlBlock(leaf).ToBytes()));

           receivedCheckpointTx.Inputs[(int) input.Index].FinalScriptWitness = new WitScript(witness.ToArray());
           
           return receivedCheckpointTx;
        }



        public async Task<Ark.V1.FinalizeTxResponse> SubmitArkTransaction(
            ArkCoinWithSigner[] arkCoins,
             Ark.V1.ArkService.ArkServiceClient arkServiceClient,
            PSBT arkTx,
            PSBT[] checkpoints,
            Network network,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Submitting Ark transaction with {CheckpointsCount} checkpoints", 
                checkpoints.Length);
            
            // Submit the transaction
            var submitRequest = new Ark.V1.SubmitTxRequest
            {
                SignedArkTx = arkTx.ToPSBTv0().ToBase64()
            };
            submitRequest.CheckpointTxs.AddRange(checkpoints.Select(x => x.ToPSBTv0().ToBase64()));
            
            _logger.LogDebug("Sending SubmitTx request to Ark service");
            var response = await arkServiceClient.SubmitTxAsync(submitRequest, cancellationToken: cancellationToken);
            _logger.LogDebug("Received SubmitTx response with Ark txid: {ArkTxid}", response.ArkTxid);
            
            // Process the signed checkpoints from the server
            var parsedReceivedCheckpoints = response.SignedCheckpointTxs
                .Select(x => PSBT.Parse(x, network).ToPSBTv2())
                .ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());
                
            var unsignedCheckpoints = checkpoints
                .ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());
                
            // Combine client and server signatures
            _logger.LogDebug("Combining client and server signatures for {CheckpointsCount} checkpoints", 
                unsignedCheckpoints.Count);
            
            List<PSBT> signedCheckpoints = new List<PSBT>();
            foreach (var signedCheckpoint in unsignedCheckpoints)
            {
                var coin = arkCoins.Single(x => x.Outpoint == signedCheckpoint.Value.Inputs.Single().PrevOut);
                signedCheckpoints .Add(await FinalizeCheckpointTx(signedCheckpoint.Value, parsedReceivedCheckpoints[signedCheckpoint.Key],coin, cancellationToken));
            }
            
            // Finalize the transaction
            var finalizeTxRequest = new Ark.V1.FinalizeTxRequest
            {
                ArkTxid = response.ArkTxid
            };
            finalizeTxRequest.FinalCheckpointTxs.AddRange(
                signedCheckpoints.Select(x => x.ToPSBTv0().ToBase64()));
                
            _logger.LogDebug("Sending FinalizeTx request to Ark service");
            var finalizeResponse = await arkServiceClient.FinalizeTxAsync(finalizeTxRequest, cancellationToken: cancellationToken);
            _logger.LogInformation("Transaction finalized successfully. Ark txid: {ArkTxid}", response.ArkTxid);
            
            return finalizeResponse;
        }
        

        /// <summary>
        /// Constructs an Ark transaction with checkpoint transactions for each input
        /// </summary>
        /// <param name="coins">Collection of coins and their respective signers</param>
        /// <param name="outputs">Output transactions</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The Ark transaction and checkpoint transactions with their input witnesses</returns>
        public async Task<(PSBT arkTx, PSBT[] checkpoints)> ConstructArkTransaction(
            ArkCoinWithSigner[] coins,
            TxOut[] outputs,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Constructing Ark transaction with {CoinsCount} coins and {OutputsCount} outputs", 
                coins.Length, outputs.Length);
            
            var p2a = Script.FromHex("51024e73"); // Standard Ark protocol marker

            List<PSBT> checkpoints = new();
            List<ArkCoinWithSigner> checkpointCoins = new();

            // Create checkpoint transactions for each input coin
            foreach (var coin in coins)
            {
                _logger.LogDebug("Creating checkpoint for coin with outpoint {Outpoint}", coin.Outpoint);
                
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

                var checkpointTx = (PSBT2)checkpoint.BuildPSBT(false, PSBTVersion.PSBTv2);


                var psbtInput = (PSBT2Input) checkpointTx.Inputs.FindIndexedInput(coin.Outpoint)!;
                // Add Ark PSBT fields
                psbtInput.Unknown.SetArkField(coin.Contract.GetTapTree());
                
                // Get signature hash for the input
                var tapLeaf = GetCollaborativePathLeaf(coin.Contract);

                psbtInput.SetTaprootLeafScript(coin.Contract.GetTaprootSpendInfo(), tapLeaf.Leaf);
                if(tapLeaf.locktime is not null)
                    psbtInput.LockTime = tapLeaf.locktime.Value;
                
                // Sign the checkpoint transaction
                // var checkpointGtx = checkpointTx.GetGlobalTransaction();
                // var checkpointPrecomputedTransactionData = 
                //     checkpointGtx.PrecomputeTransactionData([coin.TxOut]);

                // var input = checkpointGtx.Inputs.FindIndexedInput(coin.Outpoint);
                // var hash = checkpointGtx.GetSignatureHashTaproot(
                //     checkpointPrecomputedTransactionData,
                //     new TaprootExecutionData((int)input.Index, tapLeaf.Leaf.LeafHash));
                //
                // // Sign and create witness
                // _logger.LogDebug("Signing checkpoint transaction for input {InputIndex}", input.Index);
                // var sig = await coin.Signer.Sign(
                //     hash, 
                //     coin.Contract is TweakedArkPaymentContract tweaked ? tweaked.Tweak : null, 
                //     cancellationToken);
                //
                // Add to checkpoints collection
                checkpoints.Add(checkpointTx);
                
                // Create checkpoint coin for the Ark transaction
                var txout = checkpointTx.Outputs.Single(output =>
                    output.ScriptPubKey == checkpointContract.GetArkAddress().ScriptPubKey);
                var outpoint = new OutPoint(checkpointTx.GetGlobalTransaction(), txout.Index);
                
                checkpointCoins.Add(new ArkCoinWithSigner(coin.Signer,checkpointContract, outpoint, txout.GetTxOut()!));
            }
            
            // Build the Ark transaction that spends from all checkpoint outputs
            _logger.LogDebug("Building Ark transaction with {CheckpointCount} checkpoint coins", checkpointCoins.Count);
            var arkTx = _network.CreateTransactionBuilder();
            arkTx.SetVersion(3);
            arkTx.SetFeeWeight(0);
            arkTx.DustPrevention = false;
            
            arkTx.Send(p2a, Money.Zero);
           
            arkTx.AddCoins(checkpointCoins);
            
            foreach (var output in outputs)
            {
                arkTx.Send(output.ScriptPubKey, output.Value);
            }
            
            var tx = (PSBT2) arkTx.BuildPSBT(false, PSBTVersion.PSBTv2);
            
            // Sign each input in the Ark transaction
            var gtx = tx.GetGlobalTransaction();
            var precomputedTransactionData =
                gtx.PrecomputeTransactionData(checkpointCoins.Select(x => x.TxOut).ToArray());
                
            foreach (var coin in checkpointCoins)
            {
                var contract = (GenericArkContract)coin.Contract;
                var input = (PSBT2Input) tx.Inputs.FindIndexedInput(coin.Outpoint)!;
                
                // Get collaborative path and create signature
                var collabPath = contract.GetScriptBuilders().OfType<CollaborativePathArkTapScript>().Single();
                var tapleaf = collabPath.Build();
                
                // Add Ark PSBT field
                input.Unknown.SetArkField(contract.GetTapTree());
                input.SetTaprootLeafScript(contract.GetTaprootSpendInfo(), tapleaf);
                
                
               
                var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
                    new TaprootExecutionData((int)input.Index, tapleaf.LeafHash));
                
                _logger.LogDebug("Signing Ark transaction for input {InputIndex}", input.Index);
                var sig = await coin.Signer.Sign(hash, null, cancellationToken);
                
                var ourKey = ( (NofNMultisigTapScript)contract.GetScriptBuilders().OfType<CollaborativePathArkTapScript>().Single().Condition).Owners.First();
                input.SetTaprootScriptSpendSignature(ourKey, tapleaf.LeafHash, sig);
                
            }
            
            _logger.LogInformation("Ark transaction construction completed successfully");
            return (tx, checkpoints.ToArray());
        }

        /// <summary>
        /// Creates a checkpoint contract based on the input contract type
        /// </summary>
        private ArkContract CreateCheckpointContract(ArkContract inputContract)
        {
            return inputContract switch
            {
                TweakedArkPaymentContract tweaked => CreateTweakedCheckpointContract(tweaked),
                VHTLCContract htlc => CreateHTLCCheckpointContract(htlc),
                _ => throw new NotSupportedException($"Contract type {inputContract.GetType().Name} not supported")
            };
        }

        /// <summary>
        /// Creates a checkpoint contract for a tweaked payment contract
        /// </summary>
        private GenericArkContract CreateTweakedCheckpointContract(TweakedArkPaymentContract contract)
        {
            var scriptBuilders = new List<ScriptBuilder>();
            var delay = contract.GetScriptBuilders().OfType<UnilateralPathArkTapScript>().First().Timeout;
            
            var ownerScript = new NofNMultisigTapScript([contract.OriginalUser]);
            var serverScript = new NofNMultisigTapScript([contract.Server]);
            
            scriptBuilders.Add(new UnilateralPathArkTapScript(delay, serverScript));
            scriptBuilders.Add(new CollaborativePathArkTapScript(contract.Server, ownerScript));
            
            return new GenericArkContract(contract.Server, scriptBuilders);
        }

        /// <summary>
        /// Creates a checkpoint contract for an HTLC contract
        /// </summary>
        private GenericArkContract CreateHTLCCheckpointContract(VHTLCContract htlc)
        {
            var scriptBuilders = new List<ScriptBuilder>();

            var serverScript = new NofNMultisigTapScript([htlc.Server]);
            scriptBuilders.Add(new UnilateralPathArkTapScript(htlc.UnilateralClaimDelay, serverScript));
            // Determine if this is a claim path or refund path based on the preimage
            scriptBuilders.Add(
                htlc.Preimage != null
                    ? new CollaborativePathArkTapScript(htlc.Server, new NofNMultisigTapScript([htlc.Sender]))
                    : new CollaborativePathArkTapScript(htlc.Server, new NofNMultisigTapScript([htlc.Receiver])));
            return new GenericArkContract(htlc.Server, scriptBuilders);

        }

        /// <summary>
        /// Gets the collaborative path leaf for a contract
        /// </summary>
        private (TapScript Leaf, WitScript? Condition, LockTime? locktime) GetCollaborativePathLeaf(ArkContract contract)
        {
            switch (contract)
            {
                case ArkPaymentContract arkContract:
                    return (arkContract.CollaborativePath().Build(), null, null);
                case VHTLCContract {Preimage: not null} claimHtlc:
                    return (claimHtlc.CreateClaimScript().Build(), new WitScript(Op.GetPushOp(claimHtlc.Preimage!)), null);
                case VHTLCContract refundHtlc when refundHtlc.RefundLocktime.Date > DateTime.UtcNow.Date:
                    return (refundHtlc.CreateRefundWithoutReceiverScript().Build(), null, refundHtlc.RefundLocktime);
                default:
                    throw new NotSupportedException($"Contract type {contract.GetType().Name} not supported");
            }
        }
    }
    
}
