using System.Text;
using Microsoft.Extensions.Logging;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Services;

public class WalletService : IWalletService
{
    private readonly IOperatorTermsService _operatorTermsService;
    private readonly ILogger<WalletService> _logger;
    
    public WalletService(
        IOperatorTermsService operatorTermsService,
        ILogger<WalletService> logger = null)
    {
        _operatorTermsService = operatorTermsService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WalletService>.Instance;
    }
    
    public async Task<ArkContract> DerivePaymentContractAsync(DeriveContractRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deriving payment contract for user key {UserKey}", request.User.ToHex());
        
        var operatorTerms = await _operatorTermsService.GetOperatorTerms(cancellationToken);
        _logger.LogDebug("Retrieved operator terms with signer key {SignerKey}", operatorTerms.SignerKey.ToHex());
       
        if (request.Tweak is null)
        {
            _logger.LogDebug("Creating standard ArkPaymentContract without tweak");
            return new ArkPaymentContract(operatorTerms.SignerKey, operatorTerms.UnilateralExit, request.User);
        }
      
        _logger.LogDebug("Creating TweakedArkPaymentContract with tweak {Tweak}", request.Tweak.ToHex());
        return new TweakedArkPaymentContract(
            operatorTerms.SignerKey, 
            operatorTerms.UnilateralExit, 
            request.User, 
            request.Tweak);
    }
}