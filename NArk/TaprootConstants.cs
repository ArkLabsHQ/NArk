namespace NArk;

public static class TaprootConstants
{
	public static readonly string UnspendableKeyHex =
		"0250929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0";

	public static readonly byte[] UnspendableKey = Convert.FromHexString(UnspendableKeyHex);
}