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
        TxOut[]? outputs, IArkadeWalletSigner? messageSigner = null, CancellationToken cancellationToken = default)
    {
        
        messageSigner ??= inputs.FirstOrDefault()?.Signer ?? new MemoryWalletSigner(ECPrivKey.Create(RandomUtils.GetBytes(32)));
        var signerKey = await messageSigner.GetPublicKey(cancellationToken);
        var fullPubkey = TaprootFullPubKey.Create(new TaprootInternalPubKey(signerKey.ToBytes()), null);
        var addr = new TaprootAddress(fullPubkey, network);
        var highestLockTime = inputs.MaxBy(i => i.SpendingLockTime?.Value??LockTime.Zero)!.SpendingLockTime ?? LockTime.Zero;
        var toSignTx = addr.CreateBIP322PSBT(message, 0U,highestLockTime, 0U, inputs);

        if (outputs is not null && outputs.Length != 0)
        {
            
            var tx =toSignTx.GetGlobalTransaction();
            //BIP322 dictates to have only one output which is an op_return
            //however, we have a deviation from this rule in order to be able to use the intent as an output declration
            tx.Outputs.RemoveAt(0);
            tx.Outputs.AddRange(outputs);
            toSignTx = PSBT.FromTransaction(tx, network).UpdateFrom(toSignTx);
        }

        var toSignGTx = toSignTx.GetGlobalTransaction();
        var precomputedTransactionData = toSignGTx.PrecomputeTransactionData(inputs.Select(i => i.TxOut).ToArray());
        
        foreach (var inCoin in inputs)
        {
            var psbtInput = toSignTx.Inputs.FindIndexedInput(inCoin.Outpoint);

            await inCoin.SignAndFillPSBT(toSignTx, precomputedTransactionData, cancellationToken);
            
            psbtInput!.Sequence = inCoin.SpendingSequence ?? Sequence.Final;
            psbtInput.SetArkFieldTapTree(inCoin.Contract.GetTapScriptList());
            psbtInput.SetTaprootLeafScript(inCoin.Contract.GetTaprootSpendInfo(), inCoin.SpendingScript);
            if (inCoin.SpendingConditionWitness is not null)
            {
                psbtInput.SetArkFieldConditionWitness(inCoin.SpendingConditionWitness);
            }
        }

        //now we finalize and sign the first input
        var hash = toSignGTx.GetSignatureHashTaproot(precomputedTransactionData,
            new TaprootExecutionData(0));
        
        var (sig, ourKey) = await messageSigner.Sign(hash, cancellationToken);
        // var pubKey = await messageSigner.GetPublicKey(cancellationToken);
        toSignTx.Inputs[0].TaprootKeySignature = TaprootSignature.Parse(sig.ToBytes());
        toSignTx.Inputs[0].ClearForFinalize();
        toSignTx.Inputs[0].FinalizeInput();

        return toSignTx;
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

    public static async Task VerifyIntent(PSBT psbt, string message)
    {
        // if(!psbt.IsAllFinalized())
        //     throw new InvalidOperationException("PSBT is not finalized");
        //
        //
        //
        // var tx = psbt.GetGlobalTransaction();
        //
        // foreach (PSBTInput psbtInput in psbt.Inputs.Skip(1))
        // {
        //     var coin = psbtInput.GetTxOut();
        //     if (coin is null)
        //         throw new InvalidOperationException("PSBT input is not finalized");
        //     
        //     
        //     
        // }
        //
        // var toSignTx = tx;
        // var toSignPrecompute = tx.Pre
        // var toSignTxOut = new TxOut(Money.Zero, new TaprootAddress(new TaprootPubKey(toSignInput.WitScript.GetTaprootPubKey()), tx.Network));
        // var toSignCoin = new Coin(toSignInput.PrevOut, toSignTxOut);
        // var toSignPrecompute = tx.PrecomputeTransactionData(coins);
    }
    
    
    
}