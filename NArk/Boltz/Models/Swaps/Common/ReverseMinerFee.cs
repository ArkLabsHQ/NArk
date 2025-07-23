using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class ReverseMinerFee
{
    [JsonPropertyName("claim")]
    public long Claim { get; set; }

    [JsonPropertyName("lockup")]
    public long Lockup { get; set; }
}