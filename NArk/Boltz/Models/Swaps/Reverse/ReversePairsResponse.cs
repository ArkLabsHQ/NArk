using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Reverse;

public class ReversePairsResponse
{
    [JsonPropertyName("BTC")]
    public ReversePairInfo BTC { get; set; }
}

public class ReversePairInfo
{
    [JsonPropertyName("ARK")]
    public ReversePairDetails ARK { get; set; }
}

public class ReversePairDetails
{
    [JsonPropertyName("fees")]
    public ReverseFeeInfo Fees { get; set; }
    
    [JsonPropertyName("limits")]
    public ReverseLimitsInfo Limits { get; set; }
}

public class ReverseFeeInfo
{
    [JsonPropertyName("percentage")]
    public decimal Percentage { get; set; }
    
    [JsonPropertyName("minerFees")]
    public ReverseMinerFeesInfo? MinerFees { get; set; }
}

public class ReverseMinerFeesInfo
{
    [JsonPropertyName("claim")]
    public long Claim { get; set; }
    
    [JsonPropertyName("lockup")]
    public long Lockup { get; set; }
}

public class ReverseLimitsInfo
{
    [JsonPropertyName("minimal")]
    public long Minimal { get; set; }
    
    [JsonPropertyName("maximal")]
    public long Maximal { get; set; }
}
