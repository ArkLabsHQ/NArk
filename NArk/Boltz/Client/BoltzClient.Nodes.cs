using System.Net.Http.Json;
using NArk.Boltz.Models.Nodes;

namespace NArk.Boltz.Client;

public partial class BoltzClient
{
    // Nodes Endpoints

    /// <summary>
    /// Gets statistics about the Lightning nodes of the Boltz backend.
    /// </summary>
    /// <returns>Statistics about the backend's Lightning nodes.</returns>
    public async Task<NodeStats?> GetNodeStatsAsync()
    {
        return await _httpClient.GetFromJsonAsync<NodeStats>("nodes");
    }
}
