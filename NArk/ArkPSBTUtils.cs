using System.Text;
using NArk.Services;
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
	public const byte ArkPsbtFieldKeyType = 222;


	private static bool StartsWith(this byte[] bytes, byte[] prefix) => bytes.Take(prefix.Length).SequenceEqual(prefix);

	public static WitScript? GetArkFieldConditionWitness(this PSBTInput psbtInput)
	{
		var key = new[] {ArkPsbtFieldKeyType}.Concat(Encoding.UTF8.GetBytes(ConditionWitness)).ToArray();
		return !psbtInput.Unknown.TryGetValue(key, out var val) ? null : new WitScript(val);
	}

	public static TapScript[]? GetArkFieldTapTree(this PSBTInput psbtInput)
	{
		var key = new[] {ArkPsbtFieldKeyType}.Concat(Encoding.UTF8.GetBytes(VtxoTaprootTree)).ToArray();
		return !psbtInput.Unknown.TryGetValue(key, out var val) ? null : PSBTExtraConstants.DecodeTaprootTree(val);
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
			PSBTExtraConstants.EncodeTaprootTree(leaves);

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
	

	public static Sequence DecodeVtxoTreeExpiry(byte[] data)
	{
		if (data.Length == 0)
			return Sequence.Final;

		// Decode the ScriptNum value
		long scriptNumValue = (long) new CScriptNum(data, true, 6);

		return new Sequence((uint) scriptNumValue);
	}

}