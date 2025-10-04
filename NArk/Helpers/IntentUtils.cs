using System.Text.Json;
using NArk.Extensions;
using NArk.Services;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Helpers;

public class IntentUtils
{

    public static async Task<PSBT> CreateIntent(string message, Network network,  SpendableArkCoinWithSigner[] inputs,
        TxOut[] outputs, IArkadeWalletSigner? messageSigner = null, CancellationToken cancellationToken = default)
    {
        
        messageSigner ??= inputs.FirstOrDefault()?.Signer ?? new MemoryWalletSigner(ECPrivKey.Create(RandomUtils.GetBytes(32)));
        var signerKey = await messageSigner.GetXOnlyPublicKey(cancellationToken);
        var addr = new TaprootAddress(new TaprootPubKey(signerKey.ToBytes()), network);
        var highestLockTime = inputs.MaxBy(i => i.SpendingLockTime?.Value??LockTime.Zero)!.SpendingLockTime ?? LockTime.Zero;
        var toSignTx = addr.CreateBIP322PSBT(message, 0U,highestLockTime, 0U, inputs);
        foreach (var inCoin in inputs)
        {
            var psbtInput = toSignTx.Inputs.FindIndexedInput(inCoin.Outpoint);
            psbtInput!.Sequence = inCoin.SpendingSequence ?? Sequence.Final;
        }
        
        var tx =toSignTx.GetGlobalTransaction();
        
        
        if (outputs is not null && outputs.Length != 0)
        {
            //BIP322 dictates to have only one output which is an op_return
            //however, we have a decviation from this rule in order to be able to use the intent as an output declration
            tx.Outputs.RemoveAt(0);
            tx.Outputs.AddRange(outputs);
            toSignTx = PSBT.FromTransaction(tx, network).UpdateFrom(toSignTx);
        }
        
        
        //now we sign
        var toSpendInput = toSignTx.Inputs.ElementAt(0);
        var toSpendTxOut = new TxOut(Money.Zero, addr.ScriptPubKey);
        var toSpendCoin = new Coin(toSpendInput.PrevOut, toSpendTxOut);

        Coin[] coins = [toSpendCoin, ..inputs];
        var toSignPrecompute = 
            tx.PrecomputeTransactionData(coins);

        foreach (var input in tx.Inputs.AsIndexedInputs())
        {
            
            var coin = coins.Single(coin1 => coin1.Outpoint == input.PrevOut);
            var spendableCoin = coin as SpendableArkCoinWithSigner;
            var script = spendableCoin?.SpendingScriptBuilder.Build();
            var leaf =script?.LeafHash;
            var coinSigner = spendableCoin?.Signer ?? messageSigner;
            var hash = tx.GetSignatureHashTaproot(
                toSignPrecompute,
                new TaprootExecutionData((int)input.Index, leaf));
            var sig = await coinSigner.Sign(hash, cancellationToken);
            var witness = new List<Op>
            {
                Op.GetPushOp(sig.Item1.ToBytes())
            };
            if (spendableCoin?.SpendingConditionWitness is not null)
            {
                witness.AddRange(spendableCoin.SpendingConditionWitness.ToScript().ToOps());
            }
            if (spendableCoin?.SpendingScriptBuilder is not null)
            {
                var controlBlock = spendableCoin.Contract.GetTaprootSpendInfo().GetControlBlock(script);
                witness.AddRange([Op.GetPushOp(script.Script.ToBytes()), Op.GetPushOp(controlBlock.ToBytes())]);
            }
            toSignTx.Inputs.FindIndexedInput(input.PrevOut)!.FinalScriptWitness = new WitScript(witness.ToArray());
        }
        
        return toSignTx.Finalize();
    }
    
    
    public static async Task<(PSBT register, PSBT delete, RegisterIntentMessage registerMessage, DeleteIntentMessage deleteMessage)> CreateIntent(Network network,ECXOnlyPubKey[] cosigners, DateTimeOffset validAt, DateTimeOffset expireAt, SpendableArkCoinWithSigner[] ins,
        IntentTxOut[]? outs = null, IArkadeWalletSigner? signer = null,  CancellationToken cancellationToken = default)
    {
        var msg = new RegisterIntentMessage
        {
            Type = "register",
            InputTapTrees = ins.Select(i => i.Outpoint.Hash.ToString()).ToArray(),
            OnchainOutputsIndexes = outs?.Where(o => o.Type == IntentTxOut.IntentOutputType.OnChain).Select((_, i) => i).ToArray() ?? [],
            ValidAt = validAt.ToUnixTimeSeconds(),
            ExpireAt = expireAt.ToUnixTimeSeconds(),
            CosignersPublicKeys = cosigners.Select(c => c.ToHex()).ToArray()
        };

        var deleteMsg = new DeleteIntentMessage()
        {
            Type = "delete",
            ExpireAt = expireAt.ToUnixTimeSeconds()
        };
        var message = JsonSerializer.Serialize(msg);
        var deleteMessage = JsonSerializer.Serialize(deleteMsg);

        return (
            await CreateIntent(message, network, ins, outs, signer, cancellationToken),
            await CreateIntent(deleteMessage, network, ins, null, signer, cancellationToken),
            msg,
            deleteMsg);
    }
}