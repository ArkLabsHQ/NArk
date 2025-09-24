using Ark.V1;
using NArk.Models;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Extensions;

public static class ArkExtensions
{
    public static ECXOnlyPubKey ServerKey(this GetInfoResponse response)
    {
        return response.SignerPubkey.ToECXOnlyPubKey();
    }
    
    public static Sequence UnilateralExitSequence(this GetInfoResponse response)
    {
        return new Sequence(TimeSpan.FromSeconds(response.UnilateralExitDelay));
    }

    public static ArkOperatorTerms ArkOperatorTerms(this GetInfoResponse response)
    {
        return new ArkOperatorTerms(
            Dust: Money.Satoshis(response.Dust),
            SignerKey: response.ServerKey(),
            Network: Network.GetNetwork(response.Network)?? (response.Network.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase)? Network.Main : null),
            UnilateralExit: response.UnilateralExitSequence());
    }
}

