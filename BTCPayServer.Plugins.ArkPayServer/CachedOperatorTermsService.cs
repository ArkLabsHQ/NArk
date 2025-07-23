using Ark.V1;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NArk.Services;
using NArk.Services.Models;

namespace BTCPayServer.Plugins.ArkPayServer;

public class CachedOperatorTermsService(ArkService.ArkServiceClient arkClient, ILogger<OperatorTermsService> logger, IMemoryCache memoryCache) : OperatorTermsService(arkClient, logger)
{
    public override async Task<ArkOperatorTerms> GetOperatorTerms(CancellationToken cancellationToken = default)
    {
        return (await memoryCache.GetOrCreateAsync<ArkOperatorTerms>("OperatorTerms", async entry =>
        {
            var terms = await base.GetOperatorTerms(cancellationToken);
            entry.AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(1);
            return terms;
        }))!;
    }
}