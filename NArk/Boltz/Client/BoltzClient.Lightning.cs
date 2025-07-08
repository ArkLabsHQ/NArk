namespace NArk.Wallet.Boltz;

using System.Collections.Generic;
using System.Net.Http.Json;
using System.Threading.Tasks;

public partial class BoltzClient
{
    // Lightning Endpoints

    /// <summary>
    /// Gets information about channels of a Lightning node.
    /// </summary>
    /// <param name="currency">The currency of the Lightning network (e.g., "BTC").</param>
    /// <param name="node">The public key of the Lightning node.</param>
    /// <returns>A list of channel information objects.</returns>
    public async Task<List<ChannelInfo>?> GetLightningChannelInfoAsync(string currency, string node)
    {
        return await _httpClient.GetFromJsonAsync<List<ChannelInfo>>($"lightning/{currency}/channels/{node}");
    }

    /// <summary>
    /// Creates a BOLT12 offer.
    /// </summary>
    /// <param name="currency">The currency for the offer.</param>
    /// <param name="request">The BOLT12 offer creation request.</param>
    /// <returns>The created BOLT12 offer response.</returns>
    public async Task<Bolt12OfferResponse?> CreateBolt12OfferAsync(string currency, Bolt12OfferRequest request)
    {
        var response = await PostAsJsonAsync($"lightning/{currency}/bolt12", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Bolt12OfferResponse>();
    }

    /// <summary>
    /// Deletes a BOLT12 offer.
    /// </summary>
    /// <param name="currency">The currency of the offer to delete.</param>
    /// <param name="request">The BOLT12 delete request containing the offer string.</param>
    public async Task DeleteBolt12OfferAsync(string currency, Bolt12DeleteRequest request)
    {
        var response = await PostAsJsonAsync($"lightning/{currency}/bolt12/delete", request);
        response.EnsureSuccessStatusCode(); // Will throw for non-204 on failure, or do nothing on 204 success.
    }

    /// <summary>
    /// Fetches a BOLT12 invoice.
    /// </summary>
    /// <param name="currency">The currency of the invoice to fetch.</param>
    /// <param name="request">The BOLT12 fetch request.</param>
    /// <returns>The fetched BOLT12 invoice response.</returns>
    public async Task<Bolt12FetchResponse?> FetchBolt12InvoiceAsync(string currency, Bolt12FetchRequest request)
    {
        var response = await PostAsJsonAsync($"lightning/{currency}/bolt12/fetch", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Bolt12FetchResponse>();
    }
}
