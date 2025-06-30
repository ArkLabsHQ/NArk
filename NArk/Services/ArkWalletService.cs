using System.Text;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Services;

public class ArkWalletService(
    IArkOperatorTermsService operatorTermsService)
    : IArkWalletService
{
    public ECXOnlyPubKey GetXOnlyPubKeyFromWallet(string wallet)
    {
        ECXOnlyPubKey? pubKey;
        
        var encoder = Bech32Encoder.ExtractEncoderFromString(wallet);
        encoder.StrictLength = false;
        encoder.SquashBytes = true;
        var keyData = encoder.DecodeDataRaw(wallet, out _);
        switch (Encoding.UTF8.GetString(encoder.HumanReadablePart))
        {
            case "nsec":
                pubKey = ECPrivKey.Create(keyData).CreateXOnlyPubKey();
                break;
            case "npub":
                pubKey = ECXOnlyPubKey.Create(keyData);
                break;
            default:
                throw new NotSupportedException();
        }
        return pubKey;
    }
    
    public async Task<ArkContract> DerivePaymentContractAsync(DeriveContractRequest request, CancellationToken cancellationToken = default)
    {
        // Use provided tweak or generate a random one
        var tweak = request.Tweak ?? RandomUtils.GetBytes(32);
        if (tweak is null)
        {
            throw new Exception("Could not derive preimage randomly");
        }

        var pubKey = GetXOnlyPubKeyFromWallet(request.Wallet);

        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        var paymentContract = new TweakedArkPaymentContract(
            operatorTerms.SignerKey, 
            operatorTerms.UnilateralExit, 
            pubKey, 
            tweak);

        return paymentContract;
    }
}