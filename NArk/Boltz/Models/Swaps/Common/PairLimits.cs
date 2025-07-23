using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class PairLimits
{
    [JsonPropertyName("minimal")]
    public long Minimal { get; set; }

    [JsonPropertyName("maximal")]
    public long Maximal { get; set; }

    [JsonPropertyName("maximalZeroConf")]
    public MaximalZeroConf? MaximalZeroConf { get; set; }
}