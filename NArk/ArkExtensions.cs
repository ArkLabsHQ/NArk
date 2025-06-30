using Ark.V1;
using NArk.Wallet;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk;

public static class ArkExtensions
{
    public static ECXOnlyPubKey ServerKey(this GetInfoResponse response)
    {
        // Convert hex string to bytes
        var bytes = Convert.FromHexString(response.Pubkey);

        // If the server returns a standard compressed key (33 bytes),
        // remove the first byte (02 or 03) to get the 32-byte x-only key.
        if (bytes.Length == 33 && (bytes[0] == 0x02 || bytes[0] == 0x03))
        {
            bytes = bytes[1..];
        }

        return ECXOnlyPubKey.Create(bytes);
    }
    
    public static Sequence UnilateralExitSequence(this GetInfoResponse response)
    {
        return new Sequence(TimeSpan.FromSeconds(response.UnilateralExitDelay));
    }

    public static ArkOperatorTerms ArkOperatorTerms(this GetInfoResponse response)
    {
        return new ArkOperatorTerms()
        {
            Network = Network.GetNetwork(response.Network),
            SignerKey = response.ServerKey(),
            UnilateralExit = response.UnilateralExitSequence()
        };
    }

    public static string ToHex(this byte[] value)
    {
        return Convert.ToHexString(value).ToLowerInvariant();
    }
}

