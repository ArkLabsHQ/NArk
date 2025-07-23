using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class MinerFees
{
    [JsonPropertyName("baseAsset")]
    public MinerFeeDetails BaseAsset { get; set; }

    [JsonPropertyName("quoteAsset")]
    public MinerFeeDetails QuoteAsset { get; set; }
}