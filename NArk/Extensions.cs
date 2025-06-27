using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk;

public static class Extensions
{
    public static Key ToKey(this ECPrivKey key)
    {
        var bytes = new Span<byte>();
        key.WriteToSpan(bytes);
        return new Key(bytes.ToArray());
    }
    public static ECPrivKey ToEcPrivKey(this Key key)
    {
        return ECPrivKey.Create(key.ToBytes());
    }
}