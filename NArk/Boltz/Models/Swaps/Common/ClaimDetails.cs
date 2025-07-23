using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class ClaimDetails
{
    [JsonPropertyName("outputDescriptor")]
    public string OutputDescriptor { get; set; }

    [JsonPropertyName("script")]
    public string Script { get; set; }
}
