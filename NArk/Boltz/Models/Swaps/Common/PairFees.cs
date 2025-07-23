using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class PairFees
{
    [JsonPropertyName("percentage")]
    public double Percentage { get; set; }

    [JsonPropertyName("percentageSwapIn")]
    public double? PercentageSwapIn { get; set; } // For reverse swaps

    [JsonPropertyName("percentageSwapOut")]
    public double? PercentageSwapOut { get; set; } // For submarine swaps

    [JsonPropertyName("minerFees")]
    public MinerFees MinerFees { get; set; }
}