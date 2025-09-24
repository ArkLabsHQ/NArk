using NArk.Models;

namespace NArk.Services.Abstractions;

public interface IOperatorTermsService
{
    Task<ArkOperatorTerms> GetOperatorTerms(CancellationToken cancellationToken = default);
}