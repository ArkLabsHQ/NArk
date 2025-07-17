using NBitcoin;

namespace NArk.Wallet.Boltz;

using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

public partial class BoltzClient
{
    // Swap Endpoints

    /// <summary>
    /// Gets information about all supported swap pairs.
    /// </summary>
    /// <returns>A dictionary of pair ID to pair information.</returns>
    public async Task<Dictionary<string, PairInfo>?> GetPairsAsync()
    {
        return await _httpClient.GetFromJsonAsync<Dictionary<string, PairInfo>>("swap/pairs");
    }

    /// <summary>
    /// Gets information about a specific swap pair.
    /// </summary>
    /// <param name="pairId">The ID of the pair (e.g., "BTC/LNBTC").</param>
    /// <returns>Information about the specified pair.</returns>
    public async Task<PairInfo?> GetPairInfoAsync(string pairId)
    {
        return await _httpClient.GetFromJsonAsync<PairInfo>($"swap/pairs/{pairId}");
    }

    /// <summary>
    /// Gets the status of a swap.
    /// </summary>
    /// <param name="swapId">The ID of the swap.</param>
    /// <returns>The status response for the swap.</returns>
    public async Task<SwapStatusResponse?> GetSwapStatusAsync(string swapId)
    {
        return await _httpClient.GetFromJsonAsync<SwapStatusResponse>($"swap/{swapId}");
    }

    // Submarine Swaps

    /// <summary>
    /// Creates a new Submarine Swap.
    /// </summary>
    /// <param name="request">The submarine swap creation request.</param>
    /// <returns>The submarine swap response.</returns>
    public async Task<SubmarineResponse?> CreateSubmarineSwapAsync(SubmarineRequest request)
    {
        var response = await PostAsJsonAsync("swap/submarine", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SubmarineResponse>();
    }

    /// <summary>
    /// Sets the invoice for an existing Submarine Swap.
    /// </summary>
    /// <param name="swapId">The ID of the submarine swap.</param>
    /// <param name="invoice">The invoice to set (can be an empty object if invoice was part of creation).</param>
    public async Task SetSubmarineSwapInvoiceAsync(string swapId, SubmarineInvoiceRequest invoice)
    {
        var response = await PostAsJsonAsync($"swap/submarine/{swapId}/invoice", invoice);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Gets claim details for a Submarine Swap (Taproot only).
    /// </summary>
    /// <param name="swapId">The ID of the submarine swap.</param>
    /// <param name="request">The request containing transaction details for claiming.</param>
    /// <returns>The claim details response with partial signature.</returns>
    public async Task<SubmarineClaimDetailsResponse?> GetSubmarineSwapClaimDetailsAsync(string swapId, SubmarineClaimDetailsRequest request)
    {
        var response = await PostAsJsonAsync($"swap/submarine/{swapId}/claim", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SubmarineClaimDetailsResponse>();
    }

    // Reverse Swaps

    /// <summary>
    /// Creates a new Reverse Swap.
    /// </summary>
    /// <param name="request">The reverse swap creation request.</param>
    /// <returns>The reverse swap response.</returns>
    public async Task<ReverseResponse?> CreateReverseSwapAsync(ReverseRequest request)
    {
        // TODO: Temporarily mock Boltz response
        // return new ReverseResponse
        // {
        //     RefundPublicKey = "030c589fb03ca4d931f632484fd87ce859ea2f4834a13b080a765ce24a07281081",
        //     LockupAddress = "bc1phw9aky735q2lqfm024th32dfayqlvk2xgnvaqwwzef5nt7snx6nqkjsmuu",
        //     TimeoutBlockHeight = 905224,
        //     Invoice =
        //         "lnbcrt500u1p58z8p9pp5mpq6egyhmdjg30q5kjugdskr7dpctm475l9hdyc3sz5cqxl9ydksdqqcqzzsxqyz5vqsp54hs4slwmk5x3q4yaa6wvfhqh3ruyxdnev3pdge2vk9za5jkz5ynq9qxpqysgq5ht9q9cnx4gh36y76uw9g55ynrl4ajqavatq7wjds6mvxg6r654sdkr08f0xl0t7y86p39eqzlhgxca7he3yt8vf56r84ce5u4xllmsqhezv2r",
        //     OnchainAmount = 29388,
        //     Id = Guid.NewGuid().ToString()
        // };
        var response = await PostAsJsonAsync("swap/reverse", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReverseResponse>();
    }

    /// <summary>
    /// Gets claim details for a Reverse Swap (Taproot only).
    /// </summary>
    /// <param name="swapId">The ID of the reverse swap.</param>
    /// <param name="request">The request containing transaction details for claiming.</param>
    /// <returns>The claim details response with partial signature (reuses SubmarineClaimDetailsResponse structure).</returns>
    public async Task<SubmarineClaimDetailsResponse?> GetReverseSwapClaimDetailsAsync(string swapId, SubmarineClaimDetailsRequest request)
    {
        var response = await PostAsJsonAsync($"swap/reverse/{swapId}/claim", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SubmarineClaimDetailsResponse>();
    }

    // Chain Swaps

    /// <summary>
    /// Creates a new Chain Swap.
    /// </summary>
    /// <param name="request">The chain swap creation request.</param>
    /// <returns>The chain swap response.</returns>
    public async Task<ChainResponse?> CreateChainSwapAsync(ChainRequest request)
    {
        var response = await PostAsJsonAsync("swap/chain", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChainResponse>();
    }

    /// <summary>
    /// Sets the user's lockup transaction for a Chain Swap.
    /// </summary>
    /// <param name="swapId">The ID of the chain swap.</param>
    /// <param name="request">The request containing the transaction hex.</param>
    /// <returns>The updated user lockup details.</returns>
    public async Task<ChainLockupDetails?> SetChainSwapUserTransactionAsync(string swapId, ChainSetTransactionRequest request)
    {
        var response = await PostAsJsonAsync($"swap/chain/{swapId}/user", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChainLockupDetails>();
    }

    /// <summary>
    /// Sets the server's lockup transaction for a Chain Swap.
    /// </summary>
    /// <param name="swapId">The ID of the chain swap.</param>
    /// <param name="request">The request containing the transaction hex.</param>
    /// <returns>The updated server lockup details.</returns>
    public async Task<ChainLockupDetails?> SetChainSwapServerTransactionAsync(string swapId, ChainSetTransactionRequest request)
    {
        var response = await PostAsJsonAsync($"swap/chain/{swapId}/server", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChainLockupDetails>();
    }
}
