using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class MinerFeeDetails
{
    [JsonPropertyName("normal")]
    public long Normal { get; set; }

    [JsonPropertyName("reverse")]
    public ReverseMinerFee Reverse { get; set; }

    [JsonPropertyName("claim")]
    public long? Claim { get; set; } // For submarine swaps

    [JsonPropertyName("lockup")]
    public long? Lockup { get; set; } // For reverse swaps
}