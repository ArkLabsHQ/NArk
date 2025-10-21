using Ark.V1;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NArk.Services;
using NArk.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class CachedOperatorTermsService(ArkService.ArkServiceClient arkClient, ILogger<OperatorTermsService> logger, IMemoryCache memoryCache) : OperatorTermsService(arkClient, logger)
{
    public override async Task<ArkOperatorTerms> GetOperatorTerms(CancellationToken cancellationToken = default)
    {
        var terms = await memoryCache.GetOrCreateAsync<ArkOperatorTerms>("OperatorTerms", async entry =>
        {
            var terms = await base.GetOperatorTerms(cancellationToken);
            entry.AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(1);
            return terms;
        });

        if (terms is null)
        {
            memoryCache.Remove("OperatorTerms");
            throw new InvalidOperationException("Failed to fetch operator terms");
        }

        return terms;
    }
}