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
        return await _httpClient.GetFromJsonAsync<VersionResponse>("v2/version");
    }

}
