using System.Text;
using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Secp256k1;

namespace NArk;

/**
* ArkPsbtFieldKey is the key values for ark psbt fields.
*/
public static class ArkPSBTUtils
{
	public const string VtxoTaprootTree = "taptree";
	public const string VtxoTreeExpiry = "expiry";
	public const string Cosigner = "cosigner";
	public const string ConditionWitness = "condition";
	public const byte ArkPsbtFieldKeyType = 255;


	private static bool StartsWith(this byte[] bytes, byte[] prefix) => bytes.Take(prefix.Length).SequenceEqual(prefix);

	public static WitScript? GetArkFieldConditionWitness(this PSBTInput psbtInput)
	{
		var key = new[] {ArkPsbtFieldKeyType}.Concat(Encoding.UTF8.GetBytes(ConditionWitness)).ToArray();
		return !psbtInput.Unknown.TryGetValue(key, out var val) ? null : new WitScript(val);
	}

	public static TapScript[]? GetArkFieldTapTree(this PSBTInput psbtInput)
	{
		var key = new[] {ArkPsbtFieldKeyType}.Concat(Encoding.UTF8.GetBytes(VtxoTaprootTree)).ToArray();
		return !psbtInput.Unknown.TryGetValue(key, out var val) ? null : DecodeTaprootTree(val);
	}


	/// <summary>
	/// Gets VTXO tree expiry from a PSBT input
	/// </summary>
	public static Sequence? GetVtxoTreeExpiry(this PSBTInput psbtInput)
	{
		var key = new[] {ArkPsbtFieldKeyType}
			.Concat(Encoding.UTF8.GetBytes(VtxoTreeExpiry))
			.ToArray();

		if (!psbtInput.Unknown.TryGetValue(key, out var value))
			return null;

		return DecodeVtxoTreeExpiry(value);
	}



	/// <summary>
	/// Gets all cosigner public keys from a PSBT input
	/// </summary>
	public static List<CosignerPublicKeyData> GetArkFieldsCosigners(this PSBTInput psbtInput)
	{
		var cosignerPrefix = new[] {ArkPsbtFieldKeyType}
			.Concat(Encoding.UTF8.GetBytes(Cosigner))
			.ToArray();

		return psbtInput.Unknown.Where(pair => pair.Key.StartsWith(cosignerPrefix)).Select(pair =>
			new CosignerPublicKeyData
			{
				Index = pair.Key[^1],
				Key = ECPubKey.Create(pair.Value)
			}).ToList();
	}

	public static void SetArkFieldConditionWitness(this PSBTInput psbtInput, WitScript script) =>
		psbtInput.Unknown[new[] {ArkPsbtFieldKeyType}.Concat(Encoding.UTF8.GetBytes(ConditionWitness)).ToArray()] =
			script.ToBytes();

	public static void SetArkFieldTapTree(this PSBTInput psbtInput, TapScript[] leaves) =>
		psbtInput.Unknown[new[] {ArkPsbtFieldKeyType}.Concat(Encoding.UTF8.GetBytes(VtxoTaprootTree)).ToArray()] =
			EncodeTaprootTree(leaves);

	public static void SetArkFieldTreeExpiry(this PSBTInput psbtInput, Sequence expiry) =>
		psbtInput.Unknown[new[] {ArkPsbtFieldKeyType}.Concat(Encoding.UTF8.GetBytes(VtxoTreeExpiry)).ToArray()] =
			new CScriptNum(expiry.Value).getvch();

	public static void SetArkFieldCosigner(this PSBTInput psbtInput, CosignerPublicKeyData cosignerPublicKeyData)
	{
		var key = new[] {ArkPsbtFieldKeyType}
			.Concat(Encoding.UTF8.GetBytes(Cosigner))
			.Append(cosignerPublicKeyData.Index)
			.ToArray();

		psbtInput.Unknown[key] = cosignerPublicKeyData.Key.ToBytes();
	}





	/// <summary>
	/// Encodes a collection of taproot script leaves into a byte array
	/// </summary>
	/// <param name="leaves">Array of tapscript byte arrays</param>
	/// <returns>Encoded taproot tree as byte array</returns>
	public static byte[] EncodeTaprootTree(TapScript[] leaves)
	{
		var chunks = new List<byte[]>();
		// Write number of leaves as compact size uint
		chunks.Add(new VarInt((ulong) leaves.Length).ToBytes());
		foreach (var tapscript in leaves)
		{
			// Write depth (always 1 for now)
			chunks.Add([1]);
			// Write leaf version (0xc0 for tapscript)
			chunks.Add([0xc0]);
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
	/// Decodes a byte array into a collection of taproot script leaves
	/// </summary>
	/// <param name="data">Encoded taproot tree byte array</param>
	/// <returns>Array of decoded TapScript objects</returns>
	public static TapScript[] DecodeTaprootTree(byte[]? data)
	{
		if (data == null || data.Length == 0)
			return new TapScript[0];

		var bitcoinStream = new BitcoinStream(data);
		uint leavesCount = 0;
		bitcoinStream.ReadWriteAsVarInt(ref leavesCount);
		var result = new TapScript[leavesCount];

		for (int i = 0; i < leavesCount; i++)
		{
			// Read depth (should be 1)
			byte depth = 0;
			bitcoinStream.ReadWrite(ref depth);
			if (depth != 1)
				throw new FormatException($"Unexpected depth value: {depth}. Expected 1.");

			// Read leaf version (should be 0xc0 for tapscript)
			byte leafVersion = 0;
			bitcoinStream.ReadWrite(ref leafVersion);
			if (leafVersion != 0xc0)
				throw new FormatException($"Unexpected leaf version: {leafVersion:X2}. Expected 0xC0.");

			uint scriptLength = 0;
			bitcoinStream.ReadWriteAsVarInt(ref scriptLength);
			// Read script
			byte[] scriptBytes = new byte[scriptLength];
			bitcoinStream.ReadWrite(scriptBytes, 0, (int) scriptLength);

			// Create TapScript object
			result[i] = new TapScript(Script.FromBytesUnsafe(scriptBytes), (TapLeafVersion) leafVersion);
		}

		return result;
	}

	public static Sequence DecodeVtxoTreeExpiry(byte[] data)
	{
		if (data.Length == 0)
			return Sequence.Final;

		// Decode the ScriptNum value
		long scriptNumValue = (long) new CScriptNum(data, true, 6);

		return new Sequence((uint) scriptNumValue);
	}

}