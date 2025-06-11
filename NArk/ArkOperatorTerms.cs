using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Wallet;

public class ArkOperatorTerms
{
    public ECXOnlyPubKey SignerKey { get; set; }
    public Network Network { get; set; }
    public Sequence UnilateralExit { get; set; }
}