using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class SwapFailureDetails
{
    [JsonPropertyName("onchain")]
    public string? Onchain { get; set; }

    [JsonPropertyName("offchain")]
    public string? Offchain { get; set; }
}