using System.Net.Http.Json;
using NArk.Boltz.Models.Info;

namespace NArk.Boltz.Client;

public partial class BoltzClient
{
    // Info Endpoints

    /// <summary>
    /// Gets the version of the Boltz API.
    /// </summary>
    /// <returns>The API version information.</returns>
    public async Task<VersionResponse?> GetVersionAsync()
    {
        return await _httpClient.GetFromJsonAsync<VersionResponse>("version");
    }

    /// <summary>
    /// Gets warnings about the configuration of the backend.
    /// </summary>
    /// <returns>A list of warning strings.</returns>
    public async Task<List<string>?> GetWarningsAsync()
    {
        return await _httpClient.GetFromJsonAsync<List<string>>("warnings");
    }
}
