using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk;

public class ArkCoin : Coin
{
    public ArkCoin(ArkContract contract, OutPoint outpoint, TxOut txout) : base(outpoint, txout)
    {
        Contract = contract;
    }
    public ArkContract Contract { get; set; }
}

public class ArkAddress: TaprootPubKey
{
    static ArkAddress()
    {
        MainnetEncoder = Encoders.Bech32(HrpMainnet);
        MainnetEncoder.StrictLength = false;
        MainnetEncoder.SquashBytes = true;
        
        TestnetEncoder = Encoders.Bech32(HrpTestnet);
        TestnetEncoder.StrictLength = false;
        TestnetEncoder.SquashBytes = true;
    }

    protected static Bech32Encoder TestnetEncoder { get; set; }
    protected static readonly Bech32Encoder MainnetEncoder;
    protected static string HrpMainnet => "ark";
    protected static string HrpTestnet => "tark";

    public ArkAddress(TaprootAddress taprootAddress, ECXOnlyPubKey serverKey) : base(taprootAddress.PubKey.ToBytes())
    {
        ArgumentNullException.ThrowIfNull(taprootAddress);
        ArgumentNullException.ThrowIfNull(serverKey);

        ServerKey = serverKey;
    }
    
    public ArkAddress(ECXOnlyPubKey tweakedKey, ECXOnlyPubKey serverKey) : base(tweakedKey.ToBytes())
    {
        ArgumentNullException.ThrowIfNull(tweakedKey);
        ArgumentNullException.ThrowIfNull(serverKey);

        ServerKey = serverKey;
    }

    public ECXOnlyPubKey ServerKey { get; }
    
    public override string ToString()
    {
        throw new NotImplementedException();
    }
    
    public string ToString(bool mainnet)
    {
        var encoder = mainnet ? MainnetEncoder : TestnetEncoder;
        var bytes = ServerKey.ToBytes().Concat(ToBytes()).ToArray();
        return encoder.EncodeData(bytes, Bech32EncodingType.BECH32M);
    }

    public static ArkAddress Parse(string address)
    {
        address = address.ToLowerInvariant();
     
        var encoder = address.StartsWith(HrpMainnet) ? MainnetEncoder :
            address.StartsWith(HrpTestnet) ? TestnetEncoder : throw new FormatException($"Invalid Ark address: {address}");
        var data = encoder.DecodeDataRaw(address, out var type);
        
        if (type != Bech32EncodingType.BECH32M || data.Length != 64)
            throw new FormatException($"Invalid Ark address: {address}");
        
        var serverKey = ECXOnlyPubKey.Create(data.Take(32).ToArray());
        var tweakedKey = ECXOnlyPubKey.Create(data.Skip(32).ToArray());
        
        return new ArkAddress(tweakedKey, serverKey);
    }
}