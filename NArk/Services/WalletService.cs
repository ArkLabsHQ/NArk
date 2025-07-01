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