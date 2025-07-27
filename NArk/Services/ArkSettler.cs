using System.Text.Json;
using System.Text.Json.Serialization;
using Ark.V1;
using NBitcoin;
using NBitcoin.BIP322;
using NBitcoin.Secp256k1;

namespace NArk.Services;

public class RegisterIntentMessage
{
    // type: "register",
    // input_tap_trees: inputTapTrees,
    // onchain_output_indexes: onchainOutputsIndexes,
    // valid_at: nowSeconds,
    // expire_at: nowSeconds + 2 * 60, // valid for 2 minutes
    // cosigners_public_keys: cosignerPubKeys,
    
    [JsonPropertyName("type")]
    [JsonPropertyOrder(0)]
    public string Type { get; set; }
    
    [JsonPropertyName("input_tap_trees")]
    [JsonPropertyOrder(1)]
    public string[] InputTapTrees { get; set; }
    
    [JsonPropertyName("onchain_output_indexes")]
    [JsonPropertyOrder(2)]
    public int[] OnchainOutputsIndexes { get; set; }
    
    [JsonPropertyName("valid_at")]
    [JsonPropertyOrder(3)]
    public long ValidAt { get; set; }
    
    [JsonPropertyName("expire_at")]
    [JsonPropertyOrder(4)]
    public long ExpireAt { get; set; }
    
    [JsonPropertyName("cosigners_public_keys")]
    [JsonPropertyOrder(5)]
    public string[] CosignersPublicKeys { get; set; }
}

public class IntentTxOut:TxOut
{
    public enum IntentOutputType
    {
        VTXO,
        OnChain
    }
    public IntentOutputType Type { get; set; }
}

public class IntentUtils
{

    
    public static async Task<Transaction> CreateIntent(Network network,ECXOnlyPubKey[] cosigners, DateTimeOffset validAt, DateTimeOffset expireAt, SpendableArkCoinWithSigner[] ins,
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
        var message = JsonSerializer.Serialize(msg);
        
        signer ??= ins.FirstOrDefault()?.Signer ?? new MemoryWalletSigner(ECPrivKey.Create(RandomUtils.GetBytes(32)));

        var signerKey = await signer.GetPublicKey(cancellationToken);
        var addr = new TaprootAddress(new TaprootPubKey(signerKey.ToBytes()), network);
        var highestLockTime = ins.MaxBy(i => i.SpendingLockTime?.Value??LockTime.Zero)!.SpendingLockTime ?? LockTime.Zero;
        var toSignTx = addr.CreateBIP322PSBT(message, lockTime: highestLockTime, fundProofOutputs: ins);

        foreach (var inCoin in ins)
        {
            var psbtInput = toSignTx.Inputs.FindIndexedInput(inCoin.Outpoint);
            psbtInput!.Sequence = inCoin.SpendingSequence ?? Sequence.Final;
        }
        
        var tx =toSignTx.GetGlobalTransaction();
        
        
        if (outs?.Any() is true)
        {
            tx.Outputs.RemoveAt(0);
            tx.Outputs.AddRange(outs);
            toSignTx = PSBT.FromTransaction(tx, network).UpdateFrom(toSignTx);
        }
        
        //now we sign
        var toSpendInput = toSignTx.Inputs.ElementAt(0);
        var toSpendTxOut = new TxOut(Money.Zero, addr.ScriptPubKey);
        var toSpendCoin = new Coin(toSpendInput.PrevOut, toSpendTxOut);

        Coin[] coins = [toSpendCoin, ..ins];
        var toSignPrecompute = 
            tx.PrecomputeTransactionData(coins);

        foreach (var input in tx.Inputs.AsIndexedInputs())
        {
            var coin = coins.Single(coin1 => coin1.Outpoint == input.PrevOut);
var spendableCoin = coin as SpendableArkCoinWithSigner;
            var leaf =spendableCoin?.SpendingScript.LeafHash;
            var coinSigner = spendableCoin?.Signer ?? signer;
            var hash = tx.GetSignatureHashTaproot(
                toSignPrecompute,
                new TaprootExecutionData((int)input.Index, leaf));
            var sig = await coinSigner.Sign(hash, cancellationToken);
            var witness = new List<Op>();
            witness.Add(Op.GetPushOp(sig.Item1.ToBytes()));
            if (spendableCoin?.SpendingConditionWitness is not null)
            {
                witness.AddRange(spendableCoin.SpendingConditionWitness.ToScript().ToOps());
            }
            if (spendableCoin?.SpendingScript is not null)
            {
                var controlBlock = spendableCoin.Contract.GetTaprootSpendInfo().GetControlBlock(spendableCoin.SpendingScript);
                witness.AddRange([Op.GetPushOp(spendableCoin.SpendingScript.Script.ToBytes()), Op.GetPushOp(controlBlock.ToBytes())]);
            }
            toSignTx.Inputs.FindIndexedInput(input.PrevOut)!.FinalScriptWitness = new WitScript(witness.ToArray());
        }
        return toSignTx.Finalize().ExtractTransaction();
        // return (BIP322Signature.Full) BIP322Signature.FromPSBT(toSignTx, SignatureType.Full);

    }
}

public class MemoryWalletSigner : IArkadeWalletSigner
{
    private readonly ECPrivKey _key;

    public MemoryWalletSigner(ECPrivKey key)
    {
        _key = key;
    }
    public async Task<ECXOnlyPubKey> GetPublicKey(CancellationToken cancellationToken = default)
    {
        return _key.CreateXOnlyPubKey();
    }

    public async Task<(SecpSchnorrSignature, ECXOnlyPubKey)> Sign(uint256 data, CancellationToken cancellationToken = default)
    {
        var sig = _key.SignBIP340(data.ToBytes());
        return (sig, _key.CreateXOnlyPubKey());
    }
}