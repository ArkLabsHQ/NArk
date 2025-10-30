using System.Net.Http.Json;
using NArk.Boltz.Models.Swaps.Common;
using NArk.Boltz.Models.Swaps.Reverse;
using NArk.Boltz.Models.Swaps.Submarine;

namespace NArk.Boltz.Client;

public partial class BoltzClient
{

    /// <summary>
    /// Gets the status of a swap.
    /// </summary>
    /// <param name="swapId">The ID of the swap.</param>
    /// <returns>The status response for the swap.</returns>
    public async Task<SwapStatusResponse?> GetSwapStatusAsync(string swapId, CancellationToken cancellation)
    {
        return await _httpClient.GetFromJsonAsync<SwapStatusResponse>($"v2/swap/{swapId}", cancellation);
    }

    // Submarine Swaps

    /// <summary>
    /// Creates a new Submarine Swap.
    /// </summary>
    /// <param name="request">The submarine swap creation request.</param>
    /// <returns>The submarine swap response.</returns>
    public async Task<SubmarineResponse> CreateSubmarineSwapAsync(SubmarineRequest request, CancellationToken cancellation)
    {
        return await PostAsJsonAsync<SubmarineRequest, SubmarineResponse>("v2/swap/submarine", request, cancellation);
    }

    // Reverse Swaps

    /// <summary>
    /// Creates a new Reverse Swap.
    /// </summary>
    /// <param name="request">The reverse swap creation request.</param>
    /// <param name="cancellation"></param>
    /// <returns>The reverse swap response.</returns>
    public async Task<ReverseResponse?> CreateReverseSwapAsync(ReverseRequest request, CancellationToken cancellation)
    {
        return await PostAsJsonAsync<ReverseRequest, ReverseResponse>("v2/swap/reverse", request, cancellation);
    }

}
