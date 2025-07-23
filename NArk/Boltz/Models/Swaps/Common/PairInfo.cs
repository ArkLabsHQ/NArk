using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class PairInfo
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; }

    [JsonPropertyName("rate")]
    public double Rate { get; set; }

    [JsonPropertyName("limits")]
    public PairLimits Limits { get; set; }

    [JsonPropertyName("fees")]
    public PairFees Fees { get; set; }

    [JsonPropertyName("hashes")]
    public PairHashes? Hashes { get; set; } // Optional, for Taproot
}