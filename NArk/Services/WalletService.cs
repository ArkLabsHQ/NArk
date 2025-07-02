using System.Text;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Services;

public class WalletService(
    IOperatorTermsService operatorTermsService)
    : IWalletService
{
    public ECXOnlyPubKey GetXOnlyPubKeyFromWallet(string wallet)
    {
        switch (wallet.ToLowerInvariant())
        {
            case string s1 when s1.StartsWith("npub"):
                var encoder = Bech32Encoder.ExtractEncoderFromString(wallet);
                encoder.StrictLength = false;
                encoder.SquashBytes = true;
                var keyData = encoder.DecodeDataRaw(wallet, out _);
                return ECXOnlyPubKey.Create(keyData);
            default:
                throw new NotSupportedException();
        }
    }
    
    public async Task<ArkContract> DerivePaymentContractAsync(DeriveContractRequest request, CancellationToken cancellationToken = default)
    {
        
        var pubKey = GetXOnlyPubKeyFromWallet(request.Wallet);

        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);
       
        if (request.Tweak is null)
        {
            return new ArkPaymentContract(operatorTerms.SignerKey, operatorTerms.UnilateralExit, pubKey);
        }
      
        return new TweakedArkPaymentContract(
            operatorTerms.SignerKey, 
            operatorTerms.UnilateralExit, 
            pubKey, 
            request.Tweak);

    }
}