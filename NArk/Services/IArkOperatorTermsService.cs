using NArk.Wallet;

namespace NArk.Services;

public interface IArkOperatorTermsService
{
    Task<ArkOperatorTerms> GetOperatorTerms(CancellationToken cancellationToken = default);
}