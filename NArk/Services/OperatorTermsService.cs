using Ark.V1;
using Microsoft.Extensions.Logging;
using NArk.Services.Models;
using NArk.Wallet;

namespace NArk.Services;

public class OperatorTermsService(ArkService.ArkServiceClient arkClient, ILogger<OperatorTermsService> logger)
    : IOperatorTermsService
{
    private ArkOperatorTerms? _operatorTerms;

    public async Task<ArkOperatorTerms> GetOperatorTerms(CancellationToken cancellationToken = default)
    {
        if (_operatorTerms is not null)
        {
            return _operatorTerms;
        }
        
        try
        {
            var info = await arkClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
            var terms = info.ArkOperatorTerms();
            _operatorTerms = terms;
            return terms;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update operator terms.");
            throw;
        }
    }
}