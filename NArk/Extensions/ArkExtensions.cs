using Ark.V1;
using NArk.Models;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Extensions;

public static class ArkExtensions
{
    public static ECXOnlyPubKey ServerKey(this GetInfoResponse response)
    {
        return response.SignerPubkey.ToECXOnlyPubKey();
    }


    public static ArkOperatorTerms ArkOperatorTerms(this GetInfoResponse response)
    {
        var network = Network.GetNetwork(response.Network)?? (response.Network.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase)? Network.Main : null);
        
        
        if(network == null)
            throw new ArgumentException($"Unknown network {response.Network}");
        return new ArkOperatorTerms(
            Dust: Money.Satoshis(response.Dust),
            SignerKey: response.ServerKey(),
            DeprecatedSigners: response.DeprecatedSigners.ToDictionary(signer => signer.Pubkey.ToECXOnlyPubKey(),
                signer => signer.CutoffDate),
            Network: network,
            UnilateralExit: new Sequence((uint) response.UnilateralExitDelay),
            BoardingExit: new Sequence((uint) response.BoardingExitDelay),
            ForfeitAddress: BitcoinAddress.Create(response.ForfeitAddress, network),
            ForfeitPubKey: response.ForfeitPubkey.ToECXOnlyPubKey(),
            CheckpointTapscript: new CheckpointTapscript(Script.FromHex(response.CheckpointTapscript)));

    }

    class CheckpointTapscript( Script serverProvidedScript)
        : UnilateralPathArkTapScript(Sequence.Final, new NofNMultisigTapScript([]))
    {
        public override IEnumerable<Op> BuildScript()
        {
            return serverProvidedScript.ToOps();
        }
    }
}
