using System.Net.Http.Json;
using NArk.Boltz.Models.Referral;

namespace NArk.Boltz.Client;

public partial class BoltzClient
{
    // Referral Endpoints

    /// <summary>
    /// Gets the referral fees collected by the authenticated user.
    /// Requires authentication (API key in header).
    /// </summary>
    /// <returns>The referral fees response.</returns>
    public async Task<ReferralFeesResponse?> GetReferralFeesAsync()
    {
        // Note: Authentication (API key) should be handled by the HttpClient instance (e.g., in default headers).
        return await _httpClient.GetFromJsonAsync<ReferralFeesResponse>("referral/fees");
    }

    /// <summary>
    /// Gets the referral statistics for the authenticated user.
    /// Requires authentication (API key in header).
    /// </summary>
    /// <returns>The referral statistics response.</returns>
    public async Task<ReferralStatsResponse?> GetReferralStatsAsync()
    {
        // Note: Authentication (API key) should be handled by the HttpClient instance (e.g., in default headers).
        return await _httpClient.GetFromJsonAsync<ReferralStatsResponse>("referral/stats");
    }
}
