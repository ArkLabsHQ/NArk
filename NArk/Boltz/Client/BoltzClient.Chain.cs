using System.Net.Http.Json;
using NArk.Boltz.Models.Chain;

namespace NArk.Boltz.Client;

public partial class BoltzClient
{
    /// <summary>
    /// Gets fee estimations for all supported chains.
    /// </summary>
    /// <returns>A dictionary of currency to fee estimation (sat/vbyte or GWEI).</returns>
    public async Task<Dictionary<string, double>?> GetChainFeesAsync()
    {
        return await _httpClient.GetFromJsonAsync<Dictionary<string, double>>("chain/fees");
    }

    /// <summary>
    /// Gets block heights for all supported chains.
    /// </summary>
    /// <returns>A dictionary of currency to block height.</returns>
    public async Task<Dictionary<string, long>?> GetChainHeightsAsync()
    {
        return await _httpClient.GetFromJsonAsync<Dictionary<string, long>>("chain/heights");
    }

    /// <summary>
    /// Gets the network information and contract addresses for all supported EVM chains.
    /// </summary>
    /// <returns>A dictionary of currency to chain contract details.</returns>
    public async Task<Dictionary<string, ChainContractDetails>?> GetChainContractsAsync()
    {
        return await _httpClient.GetFromJsonAsync<Dictionary<string, ChainContractDetails>>("chain/contracts");
    }

    /// <summary>
    /// Gets fee estimation for a specific chain.
    /// </summary>
    /// <param name="currency">The currency of the chain (e.g., "BTC", "ETH").</param>
    /// <returns>The fee estimation for the specified currency.</returns>
    public async Task<CurrencyFeeResponse?> GetCurrencyFeeAsync(string currency)
    {
        return await _httpClient.GetFromJsonAsync<CurrencyFeeResponse>($"chain/{currency}/fee");
    }

    /// <summary>
    /// Gets block height for a specific chain.
    /// </summary>
    /// <param name="currency">The currency of the chain (e.g., "BTC", "ETH").</param>
    /// <returns>The block height for the specified currency.</returns>
    public async Task<CurrencyHeightResponse?> GetCurrencyHeightAsync(string currency)
    {
        return await _httpClient.GetFromJsonAsync<CurrencyHeightResponse>($"chain/{currency}/height");
    }

    /// <summary>
    /// Broadcasts a transaction to a specific chain.
    /// </summary>
    /// <param name="currency">The currency of the chain to broadcast to.</param>
    /// <param name="request">The broadcast request containing the transaction hex.</param>
    /// <returns>The response containing the transaction ID if successful.</returns>
    public async Task<BroadcastResponse?> BroadcastTransactionAsync(string currency, BroadcastRequest request)
    {
        var response = await PostAsJsonAsync($"chain/{currency}/transaction", request);
        response.EnsureSuccessStatusCode(); // Or handle non-success codes more gracefully
        return await response.Content.ReadFromJsonAsync<BroadcastResponse>();
    }
}
