using Ark.V1;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk;

public static class ArkExtensions
{
    public static ECPrivKey GetKeyFromWallet(string wallet)
    {
        switch (wallet.ToLowerInvariant())
        {
            case { } s2 when s2.StartsWith("nsec"):
                var encoder2 = Bech32Encoder.ExtractEncoderFromString(wallet);
                encoder2.StrictLength = false;
                encoder2.SquashBytes = true;
                var keyData2 = encoder2.DecodeDataRaw(wallet, out _);
                return ECPrivKey.Create(keyData2);
            
                
            default:
                throw new NotSupportedException();
        }
    
        
    }
    
    public static ECXOnlyPubKey GetXOnlyPubKeyFromWallet(string wallet)
    {
        switch (wallet.ToLowerInvariant())
        {
            case { } s1 when s1.StartsWith("npub"):
                var encoder = Bech32Encoder.ExtractEncoderFromString(wallet);
                encoder.StrictLength = false;
                encoder.SquashBytes = true;
                var keyData = encoder.DecodeDataRaw(wallet, out _);
                return ECXOnlyPubKey.Create(keyData);
            case { } s2 when s2.StartsWith("nsec"):
                var encoder2 = Bech32Encoder.ExtractEncoderFromString(wallet);
                encoder2.StrictLength = false;
                encoder2.SquashBytes = true;
                var keyData2 = encoder2.DecodeDataRaw(wallet, out _);
                return ECPrivKey.Create(keyData2).CreateXOnlyPubKey();
                
            default:
                throw new NotSupportedException();
        }
    }
    
    public static ECXOnlyPubKey ServerKey(this GetInfoResponse response)
    {
        return response.SignerPubkey.ToECXOnlyPubKey();
    }
    
    public static ECXOnlyPubKey ToECXOnlyPubKey(this string pubKeyHex)
    {
        var pubKey = new PubKey(pubKeyHex);
        return pubKey.ToECXOnlyPubKey();
    }
    
    public static ECXOnlyPubKey ToECXOnlyPubKey(this byte[] pubKeyBytes)
    {
        var pubKey = new PubKey(pubKeyBytes);
        return pubKey.ToECXOnlyPubKey();
    }

    public static ECXOnlyPubKey ToECXOnlyPubKey(this PubKey pubKey)
    {
        var xOnly = pubKey.TaprootInternalKey.ToBytes();
        return ECXOnlyPubKey.Create(xOnly);
    }
    
    public static string ToCompressedEvenYHex(this ECXOnlyPubKey xOnlyPubKey)
    {
        return "02" + xOnlyPubKey.ToHex();
    }
    
    public static Sequence UnilateralExitSequence(this GetInfoResponse response)
    {
        return new Sequence(TimeSpan.FromSeconds(response.UnilateralExitDelay));
    }

    public static ArkOperatorTerms ArkOperatorTerms(this GetInfoResponse response)
    {
        return new ArkOperatorTerms(
            SignerKey: response.ServerKey(),
            Network: Network.GetNetwork(response.Network),
            UnilateralExit: response.UnilateralExitSequence());
    }

    public static string ToHex(this byte[] value)
    {
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    public static string ToHex(this ECXOnlyPubKey value)
    {
        return Convert.ToHexString(value.ToBytes()).ToLowerInvariant();
    }
    
    
}

