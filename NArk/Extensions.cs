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
    public static ECPrivKey ToKey(this Key key)
    {
        return ECPrivKey.Create(key.ToBytes());
    }

    public static ECXOnlyPubKey GetXOnlyPubKey(this Key key)
    {
        return key.ToKey().CreateXOnlyPubKey();
    }


}