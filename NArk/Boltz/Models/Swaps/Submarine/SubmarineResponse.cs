using System.Text.Json.Serialization;
using NArk.Boltz.Models.Swaps.Common;

namespace NArk.Boltz.Models.Swaps.Submarine;

public class SubmarineResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("acceptZeroConf")]
    public bool? AcceptZeroConf { get; set; }

    [JsonPropertyName("expectedAmount")]
    public long ExpectedAmount { get; set; }

    [JsonPropertyName("address")]
    public string Address { get; set; }

    [JsonPropertyName("redeemScript")]
    public string RedeemScript { get; set; }

    [JsonPropertyName("blindingKey")]
    public string? BlindingKey { get; set; }

    [JsonPropertyName("timeoutBlockHeight")]
    public long TimeoutBlockHeight { get; set; }

    [JsonPropertyName("swapTree")]
    public SwapTree? SwapTree { get; set; }

    [JsonPropertyName("claimPublicKey")]
    public string? ClaimPublicKey { get; set; }
}
