using NArk.Services.Models;

namespace NArk.Services;

public interface IOperatorTermsService
{
    Task<ArkOperatorTerms> GetOperatorTerms(CancellationToken cancellationToken = default);
}