using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class SwapTransaction
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("eta")]
    public int? Eta { get; set; } // Could be string like "pending" or a timestamp/blockheight
}