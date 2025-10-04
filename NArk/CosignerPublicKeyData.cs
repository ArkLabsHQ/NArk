using NBitcoin.Secp256k1;

namespace NArk;

public class CosignerPublicKeyData
{
    public byte Index { get; set; }
    public ECPubKey Key { get; set; }
}