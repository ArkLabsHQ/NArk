using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class MaximalZeroConf
{
    [JsonPropertyName("baseAsset")]
    public long? BaseAsset { get; set; }

    [JsonPropertyName("quoteAsset")]
    public long? QuoteAsset { get; set; }
}