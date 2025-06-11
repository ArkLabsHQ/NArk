using Ark.V1;
using NArk.Wallet;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk;

public static class ArkExtensions
{
    public static ECXOnlyPubKey ServerKey(this GetInfoResponse response)
    {
        return ECXOnlyPubKey.Create(Convert.FromHexString(response.Pubkey));
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
}

