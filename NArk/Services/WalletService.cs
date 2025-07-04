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
    
    public async Task<ArkContract> DerivePaymentContractAsync(DeriveContractRequest request, CancellationToken cancellationToken = default)
    {
        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);
       
        if (request.Tweak is null)
        {
            return new ArkPaymentContract(operatorTerms.SignerKey, operatorTerms.UnilateralExit, request.User);
        }
      
        return new TweakedArkPaymentContract(
            operatorTerms.SignerKey, 
            operatorTerms.UnilateralExit, 
            request.User, 
            request.Tweak);

    }
}