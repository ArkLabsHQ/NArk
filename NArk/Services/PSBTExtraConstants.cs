using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Services;

public static class PSBTExtraConstants
{
    public static byte PSBT_IN_TAP_SCRIPT_SIG = 0x14;
    public static byte PSBT_IN_TAP_LEAF_SCRIPT = 0x15;

    public static (byte[] key, byte[] value) GetTaprootScriptSpendSignature(ECXOnlyPubKey key, uint256 leafHash,
        SecpSchnorrSignature signature)
    {
        byte[] keyBytes = [PSBT_IN_TAP_SCRIPT_SIG,..key.ToBytes(), ..leafHash.ToBytes()];
        byte[] valueBytes =  signature.ToBytes();
        return (keyBytes, valueBytes);
    }
    
    public static void SetTaprootScriptSpendSignature(this PSBTInput input, ECXOnlyPubKey key, uint256 leafHash,
        SecpSchnorrSignature signature)
    {
        var (keyBytes, valueBytes) = GetTaprootScriptSpendSignature(key, leafHash, signature);
        input.Unknown[keyBytes] = valueBytes;
    }

    public static (ECXOnlyPubKey key, uint256 leafHash, SecpSchnorrSignature signature) GetTaprootScriptSpendSignature(
        byte[] keyBytes, byte[] valueBytes)
    {
        var key = ECXOnlyPubKey.Create(keyBytes[1..33]);
        var leafHash = new uint256(keyBytes[33..65]);
        SecpSchnorrSignature.TryCreate(valueBytes, out var sig);
        return (key, leafHash, sig!);
    }

    public static (byte[] key, byte[] value) GetTaprootLeafScript( TaprootSpendInfo spendInfo, TapScript leafScript)
    {
        byte[] keyBytes = [PSBT_IN_TAP_LEAF_SCRIPT,..spendInfo.GetControlBlock(leafScript).ToBytes()];
        byte[] valueBytes = [..leafScript.Script.ToBytes(), (byte) leafScript.Version];
        return (keyBytes, valueBytes);
    }
    
    public static void SetTaprootLeafScript(this PSBTInput input, TaprootSpendInfo spendInfo, TapScript leafScript)
    {
        var (keyBytes, valueBytes) = GetTaprootLeafScript(spendInfo, leafScript);
        input.Unknown[keyBytes] = valueBytes;
        
        
    }
        
    public static (ControlBlock controlBlock, TapScript leafScript) GetTaprootLeafScript(byte[] keyBytes, byte[] valueBytes)
    {
        var controlBlock =  ControlBlock.FromSlice(keyBytes[1..65]);
        var leafScript = new TapScript(Script.FromBytesUnsafe(valueBytes[1..^1]), (TapLeafVersion)valueBytes[^1]);
        return (controlBlock, leafScript);
    }
        
        
        
}