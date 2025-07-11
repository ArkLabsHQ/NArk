using BTCPayServer.Plugins.ArkPayServer.Services;
using NBitcoin;

namespace NArk.Services
{
    /// <summary>
    /// Extension methods for working with Ark transactions
    /// </summary>
    public static class ArkTransactionExtensions
    {
        /// <summary>
        /// Creates an Ark transaction using the provided coins and outputs
        /// </summary>
        /// <param name="network">Bitcoin network</param>
        /// <param name="coins">Collection of coins with their signers</param>
        /// <param name="outputs">Transaction outputs</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The Ark transaction and checkpoint transactions with their witnesses</returns>
        public static Task<(PSBT arkTx, (PSBT checkpoint, WitScript inputWitness)[])> CreateArkTransaction(
            this Network network,
            ArkCoinWithSigner[] coins,
            TxOut[] outputs,
            CancellationToken cancellationToken)
        {
            var builder = new ArkTransactionBuilder(network);
            return builder.ConstructArkTransaction(coins, outputs, cancellationToken);
        }

        /// <summary>
        /// Creates an Ark transaction using a single coin and output
        /// </summary>
        /// <param name="network">Bitcoin network</param>
        /// <param name="signer">Signer for the coin</param>
        /// <param name="coin">Coin to spend</param>
        /// <param name="destination">Destination address</param>
        /// <param name="amount">Amount to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The Ark transaction and checkpoint transactions with their witnesses</returns>
        public static Task<(PSBT arkTx, (PSBT checkpoint, WitScript inputWitness)[])> CreateArkTransaction(
            this Network network,
            IArkadeWalletSigner signer,
            ArkCoin coin,
            BitcoinAddress destination,
            Money amount,
            CancellationToken cancellationToken)
        {
            return network.CreateArkTransaction(
                [new ArkCoinWithSigner(signer, coin.Contract, coin.Outpoint, coin.TxOut)],
                [new TxOut(amount, destination)],
                cancellationToken);
        }

        /// <summary>
        /// Creates an Ark transaction that consolidates multiple coins into a single output
        /// </summary>
        /// <param name="network">Bitcoin network</param>
        /// <param name="signer">Signer for all coins</param>
        /// <param name="coins">Coins to consolidate</param>
        /// <param name="destination">Destination address</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The Ark transaction and checkpoint transactions with their witnesses</returns>
        public static Task<(PSBT arkTx, (PSBT checkpoint, WitScript inputWitness)[])> CreateConsolidationArkTransaction(
            this Network network,
            IArkadeWalletSigner signer,
            IEnumerable<ArkCoin> coins,
            BitcoinAddress destination,
            CancellationToken cancellationToken)
        {
            var coinArray = coins.ToArray();
            var totalAmount = Money.Satoshis(coinArray.Sum(c => c.TxOut.Value));
            
            return network.CreateArkTransaction(
                (ArkCoinWithSigner[]) coinArray,
                [new TxOut(totalAmount, destination)],
                cancellationToken);
        }

        /// <summary>
        /// Submits an Ark transaction to the Ark service
        /// </summary>
        /// <param name="arkServiceClient">Ark service client</param>
        /// <param name="arkTx">Ark transaction</param>
        /// <param name="checkpoints">Checkpoint transactions with witnesses</param>
        /// <param name="network">Bitcoin network</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The finalized transaction response</returns>
        public static async Task<Ark.V1.FinalizeTxResponse> SubmitArkTransaction(
            this Ark.V1.ArkService.ArkServiceClient arkServiceClient,
            PSBT arkTx,
            (PSBT checkpoint, WitScript inputWitness)[] checkpoints,
            Network network,
            CancellationToken cancellationToken)
        {
            // Submit the transaction
            var submitRequest = new Ark.V1.SubmitTxRequest
            {
                SignedArkTx = arkTx.ToBase64()
            };
            submitRequest.CheckpointTxs.AddRange(checkpoints.Select(x => x.checkpoint.ToBase64()));
            
            var response = await arkServiceClient.SubmitTxAsync(submitRequest, cancellationToken: cancellationToken);
            
            // Process the signed checkpoints from the server
            var parsedReceivedCheckpoints = response.SignedCheckpointTxs
                .Select(x => PSBT.Parse(x, network))
                .ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());
                
            var signedCheckpoints = checkpoints
                .ToDictionary(psbt => psbt.checkpoint.GetGlobalTransaction().GetHash());
                
            // Combine client and server signatures
            foreach (var signedCheckpoint in signedCheckpoints)
            {
                var serverSig = parsedReceivedCheckpoints[signedCheckpoint.Key].Inputs[0].FinalScriptWitness.Pushes.First();
                
                signedCheckpoint.Value.checkpoint.Inputs[0].FinalScriptWitness = new WitScript(
                    signedCheckpoint.Value.inputWitness.Pushes.Concat(new[] { serverSig }).ToArray());
            }
            
            // Finalize the transaction
            var finalizeTxRequest = new Ark.V1.FinalizeTxRequest
            {
                ArkTxid = response.ArkTxid
            };
            finalizeTxRequest.FinalCheckpointTxs.AddRange(
                signedCheckpoints.Select(x => x.Value.checkpoint.ToBase64()));
                
            return await arkServiceClient.FinalizeTxAsync(finalizeTxRequest, cancellationToken: cancellationToken);
        }
    }
}
