using Microsoft.Extensions.Logging;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Services
{
    /// <summary>
    /// Utility class for building and constructing Ark transactions
    /// </summary>
    public class ArkTransactionBuilder
    {
        private readonly IOperatorTermsService _operatorTermsService;
        private readonly ILogger<ArkTransactionBuilder> _logger;

        public ArkTransactionBuilder(
            IOperatorTermsService operatorTermsService,
            ILogger<ArkTransactionBuilder> logger)
        {
            _operatorTermsService = operatorTermsService;
            _logger = logger;
        }

        public async Task<PSBT> FinalizeCheckpointTx(PSBT checkpointTx, PSBT receivedCheckpointTx, ArkCoinWithSigner coin,  CancellationToken cancellationToken)
        {
            _logger.LogDebug("Finalizing checkpoint transaction for coin with outpoint {Outpoint}", coin.Outpoint);
            
            var serverSigKv = receivedCheckpointTx.Inputs[0].Unknown
                .First(pair => pair.Key[0] == PSBTExtraConstants.PSBT_IN_TAP_SCRIPT_SIG);
            
            var serverSig = PSBTExtraConstants.GetTaprootScriptSpendSignature(serverSigKv.Key, serverSigKv.Value);
            
            // Sign the checkpoint transaction
            var checkpointGtx = receivedCheckpointTx.GetGlobalTransaction();
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
            
            var (sig, key) = await coin.Signer.Sign(
                hash, 
                cancellationToken);
            
           var (leaf, condition, locktime) =  GetCollaborativePathLeaf(coin.Contract);
           if (condition is not null)
           {
               receivedCheckpointTx.Inputs[(int) input.Index].Unknown.SetArkField(condition);
           }
           receivedCheckpointTx.Inputs[(int) input.Index].SetTaprootScriptSpendSignature(key, leaf.LeafHash, sig);
           receivedCheckpointTx.UpdateFrom(checkpointTx);
           
           return receivedCheckpointTx;
        }


public async Task ConstructAndSubmitArkTransaction(
            IEnumerable<ArkCoinWithSigner> arkCoins,
            TxOut[] arkOutputs,
             Ark.V1.ArkService.ArkServiceClient arkServiceClient,
            CancellationToken cancellationToken)
        {
            var (arkTx, checkpoints) = await ConstructArkTransaction(arkCoins, arkOutputs, cancellationToken);
            await SubmitArkTransaction(arkCoins, arkServiceClient, arkTx, checkpoints, cancellationToken);
        }   
        
        
        public async Task<Ark.V1.FinalizeTxResponse> SubmitArkTransaction(
            IEnumerable<ArkCoinWithSigner> arkCoins,
             Ark.V1.ArkService.ArkServiceClient arkServiceClient,
            PSBT arkTx,
            PSBT[] checkpoints,
            CancellationToken cancellationToken)
        {
            var network = arkTx.Network;
            _logger.LogInformation("Submitting Ark transaction with {CheckpointsCount} checkpoints", 
                checkpoints.Length);
            
            // Submit the transaction
            var submitRequest = new Ark.V1.SubmitTxRequest
            {
                SignedArkTx = arkTx.ToBase64(),
                CheckpointTxs = { checkpoints.Select(x => x.ToBase64()) }
            };
            _logger.LogDebug("Sending SubmitTx request to Ark service");
            var response = await arkServiceClient.SubmitTxAsync(submitRequest, cancellationToken: cancellationToken);
            _logger.LogDebug("Received SubmitTx response with Ark txid: {ArkTxid}", response.ArkTxid);
            
            // Process the signed checkpoints from the server
            var parsedReceivedCheckpoints = response.SignedCheckpointTxs
                .Select(x => PSBT.Parse(x, network))
                .ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());
                
            var unsignedCheckpoints = checkpoints
                .ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());
                
            // Combine client and server signatures
            _logger.LogDebug("Combining client and server signatures for {CheckpointsCount} checkpoints", 
                unsignedCheckpoints.Count);
            
            List<PSBT> signedCheckpoints = [];
            foreach (var signedCheckpoint in unsignedCheckpoints)
            {
                var coin = arkCoins.Single(x => x.Outpoint == signedCheckpoint.Value.Inputs.Single().PrevOut);
                signedCheckpoints .Add(await FinalizeCheckpointTx(signedCheckpoint.Value, parsedReceivedCheckpoints[signedCheckpoint.Key],coin, cancellationToken));
            }
            
            // Finalize the transaction
            var finalizeTxRequest = new Ark.V1.FinalizeTxRequest
            {
                ArkTxid = response.ArkTxid,
                FinalCheckpointTxs = { signedCheckpoints.Select(x => x.ToBase64()) }
            };
           
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
            IEnumerable<ArkCoinWithSigner> coins,
            TxOut[] outputs,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Constructing Ark transaction with {CoinsCount} coins and {OutputsCount} outputs", 
                coins.Count(), outputs.Length);
            
            var p2a = Script.FromHex("51024e73"); // Standard Ark protocol marker

            List<PSBT> checkpoints = new();
            List<ArkCoinWithSigner> checkpointCoins = new();
            var terms = await _operatorTermsService.GetOperatorTerms(cancellationToken);
            foreach (var coin in coins)
            {
                _logger.LogDebug("Creating checkpoint for coin with outpoint {Outpoint}", coin.Outpoint);
                
                // Create checkpoint contract
                var checkpointContract = CreateCheckpointContract(coin.Contract,terms);
                
                
                var tapLeaf = GetCollaborativePathLeaf(coin.Contract);
                // Build checkpoint transaction
                var checkpoint = terms.Network.CreateTransactionBuilder();
                checkpoint.SetVersion(3);
                checkpoint.SetFeeWeight(0);
                checkpoint.AddCoin(coin);
                checkpoint.DustPrevention = false;
                checkpoint.Send(checkpointContract.GetArkAddress(), coin.Amount);
                checkpoint.SetLockTime(tapLeaf.locktime ?? LockTime.Zero);
                var checkpointTx = checkpoint.BuildPSBT(false, PSBTVersion.PSBTv0);
           
                //checkpoints MUST have the p2a output at index 1 and NBitcoin tx builder does not assure it, so we hack our way there
                var ctx = checkpointTx.GetGlobalTransaction();
                ctx.Outputs.Add(new TxOut(Money.Zero, p2a));
                checkpointTx = PSBT.FromTransaction(ctx, terms.Network, PSBTVersion.PSBTv0);
                checkpoint.UpdatePSBT(checkpointTx);
                
                var psbtInput = checkpointTx.Inputs.FindIndexedInput(coin.Outpoint)!;
                // Add Ark PSBT fields
                psbtInput.Unknown.SetArkField(coin.Contract.GetTapScriptList());
                psbtInput.SetTaprootLeafScript(coin.Contract.GetTaprootSpendInfo(), tapLeaf.Leaf);
                checkpoints.Add(checkpointTx);
                
                // Create checkpoint coin for the Ark transaction
                var txout = checkpointTx.Outputs.Single(output =>
                    output.ScriptPubKey == checkpointContract.GetArkAddress().ScriptPubKey);
                var outpoint = new OutPoint(checkpointTx.GetGlobalTransaction(), txout.Index);
                
                checkpointCoins.Add(new ArkCoinWithSigner(coin.Signer,checkpointContract, outpoint, txout.GetTxOut()!));
            }
            
            // Build the Ark transaction that spends from all checkpoint outputs
            _logger.LogDebug("Building Ark transaction with {CheckpointCount} checkpoint coins", checkpointCoins.Count);
            var arkTx = terms.Network.CreateTransactionBuilder();
            arkTx.SetVersion(3);
            arkTx.SetFeeWeight(0);
            arkTx.DustPrevention = false;
            // arkTx.Send(p2a, Money.Zero);
            arkTx.AddCoins(checkpointCoins);
            
            foreach (var output in outputs)
            {
                arkTx.Send(output.ScriptPubKey, output.Value);
            }
            
            var tx =  arkTx.BuildPSBT(false, PSBTVersion.PSBTv0);
            var gtx = tx.GetGlobalTransaction();
            gtx.Outputs.Add(new TxOut(Money.Zero, p2a));
            tx = PSBT.FromTransaction(gtx, terms.Network, PSBTVersion.PSBTv0);
            arkTx.UpdatePSBT(tx);
            
            
            // Sign each input in the Ark transaction
            var precomputedTransactionData =
                gtx.PrecomputeTransactionData(checkpointCoins.Select(x => x.TxOut).ToArray());
                
            foreach (var coin in checkpointCoins)
            {
                var contract = (GenericArkContract)coin.Contract;
                var checkpointInput =  tx.Inputs.FindIndexedInput(coin.Outpoint)!;
                
                // Get collaborative path and create signature
                var collabPath = contract.GetScriptBuilders().OfType<CollaborativePathArkTapScript>().Single();
                var tapleaf = collabPath.Build();
                
                // Add Ark PSBT field
                checkpointInput.Unknown.SetArkField(contract.GetTapScriptList());
                checkpointInput.SetTaprootLeafScript(contract.GetTaprootSpendInfo(), tapleaf);
                
                var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
                    new TaprootExecutionData((int)checkpointInput.Index, tapleaf.LeafHash));
                
                _logger.LogDebug("Signing Ark transaction for input {InputIndex}", checkpointInput.Index);
                var (sig, ourKey) = await coin.Signer.Sign(hash,  cancellationToken);
                
                checkpointInput.SetTaprootScriptSpendSignature(ourKey, tapleaf.LeafHash, sig);
                
            }
            
            _logger.LogInformation("Ark transaction construction completed successfully");
            return (tx, checkpoints.ToArray());
        }

        /// <summary>
        /// Creates a checkpoint contract based on the input contract type
        /// </summary>
        private ArkContract CreateCheckpointContract(ArkContract inputContract, ArkOperatorTerms terms)
        {
            var server = inputContract.Server;
            var delay = terms.UnilateralExit;
            var user = inputContract switch
            {
                HashLockedArkPaymentContract hashLockedArkPaymentContract => hashLockedArkPaymentContract.User,
                ArkPaymentContract arkPaymentContract => arkPaymentContract.User,
                VHTLCContract htlc => 
                    htlc.Preimage != null
                        ?htlc.Receiver
                        : htlc.Sender,
            };
            
            return CreateCheckpointContract(delay, server, user);
            
        }

        
        private GenericArkContract CreateCheckpointContract(Sequence unilateralDelay, ECXOnlyPubKey server, ECXOnlyPubKey user)
        {
            var scriptBuilders = new List<ScriptBuilder>();

            var ownerScript = new NofNMultisigTapScript([user]);
            var serverScript = new NofNMultisigTapScript([server]);
            
            scriptBuilders.Add(new UnilateralPathArkTapScript(unilateralDelay, serverScript));
            scriptBuilders.Add(new CollaborativePathArkTapScript(server, ownerScript));
            
            return new GenericArkContract(server, scriptBuilders);
        }
        
        

        /// <summary>
        /// Gets the collaborative path leaf for a contract
        /// </summary>
        private (TapScript Leaf, WitScript? Condition, LockTime? locktime) GetCollaborativePathLeaf(ArkContract contract)
        {
            return contract switch
            {
                HashLockedArkPaymentContract hashLockedArkPaymentContract => (
                    hashLockedArkPaymentContract.CreateClaimScript().Build(), new WitScript(Op.GetPushOp(hashLockedArkPaymentContract.Preimage)), null),
                ArkPaymentContract arkContract => (arkContract.CollaborativePath().Build(), null, null),
                VHTLCContract {Preimage: not null} claimHtlc => (claimHtlc.CreateClaimScript().Build(),
                    new WitScript(Op.GetPushOp(claimHtlc.Preimage!)), null),
                VHTLCContract {RefundLocktime.IsTimeLock: true} refundHtlc when
                    refundHtlc.RefundLocktime.Date > DateTime.UtcNow.Date => (
                        refundHtlc.CreateRefundWithoutReceiverScript().Build(), null, refundHtlc.RefundLocktime),
                _ => throw new NotSupportedException($"Contract type {contract.GetType().Name} not supported")
            };
        }
    }
    
}
