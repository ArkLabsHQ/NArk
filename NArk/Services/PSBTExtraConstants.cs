using NBitcoin;
using NBitcoin.BIP370;
using NBitcoin.Protocol;
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
    public static (ControlBlock controlBlock, TapScript leafScript)[] GetTaprootLeafScript(this PSBTInput input)
    {
        return input.Unknown.Where(pair => pair.Key[0] == PSBT_IN_TAP_LEAF_SCRIPT).Select(pair => GetTaprootLeafScript(pair.Key, pair.Value)).ToArray();
    }
    
    public static (ControlBlock controlBlock, TapScript leafScript) GetTaprootLeafScript(byte[] keyBytes, byte[] valueBytes)
    {
        var controlBlock =  ControlBlock.FromSlice(keyBytes.Skip(1).ToArray());
        var leafScript = new TapScript(Script.FromBytesUnsafe(valueBytes[..^1]), (TapLeafVersion)valueBytes[^1]);
        return (controlBlock, leafScript);
    }
    
    /// <summary>
	/// Encodes a collection of taproot script leaves into a byte array following PSBT spec
	/// Format: {<depth> <version> <script_length> <script>}* (no leaf count prefix)
	/// </summary>
	/// <param name="leaves">Array of tapscript byte arrays</param>
	/// <returns>Encoded taproot tree as byte array</returns>
	public static byte[] EncodeTaprootTree(TapScript[] leaves)
	{
		var chunks = new List<byte[]>();
		foreach (var tapscript in leaves)
		{
			// Write depth (always 1 for now)
			chunks.Add([1]);
			// Write leaf version
			chunks.Add([(byte) tapscript.Version]);
			// Write script length and script
			chunks.Add(new VarInt((ulong) tapscript.Script.Length).ToBytes());
			chunks.Add(tapscript.Script.ToBytes());
		}

		// Concatenate all chunks
		int totalLength = chunks.Sum(chunk => chunk.Length);
		byte[] result = new byte[totalLength];
		int offset = 0;
		foreach (var chunk in chunks)
		{
			Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
			offset += chunk.Length;
		}

		return result;
	}

	/// <summary>
	/// Decodes an encoded taproot tree byte array back into TapScript objects following PSBT spec
	/// Format: {<depth> <version> <script_length> <script>}* (no leaf count prefix)
	/// </summary>
	/// <param name="data">Encoded taproot tree byte array</param>
	/// <returns>Array of decoded TapScript objects</returns>
	public static TapScript[] DecodeTaprootTree(byte[]? data)
	{
		if (data == null || data.Length == 0)
			return [];

		var stream = new BitcoinStream(data);
		var leaves = new List<TapScript>();
		
		// Read tuples until we run out of data
		while (stream.Inner.Position < stream.Inner.Length)
		{
			// Read depth
			var depth = (byte)stream.Inner.ReadByte();
			if (depth != 1)
			{
				throw new FormatException("Invalid depth");
			}
			// Read leaf version
			var leafVersion = (byte)stream.Inner.ReadByte();

			
			if(leafVersion != (byte)TapLeafVersion.C0)
			{
				throw new FormatException("Invalid leaf version");
			}
			
			// Read script length
			var scriptLength = (int)VarInt.StaticRead(stream);
			
			// Read script bytes
			var scriptBytes = new byte[scriptLength];
			stream.Inner.ReadExactly(scriptBytes, 0, scriptLength);

			// Create TapScript object
			leaves.Add(new TapScript(Script.FromBytesUnsafe(scriptBytes), (TapLeafVersion)leafVersion));
		}

		return leaves.ToArray();
	}
}