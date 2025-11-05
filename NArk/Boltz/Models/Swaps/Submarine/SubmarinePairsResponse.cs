using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Submarine;

public class SubmarinePairsResponse
{
    [JsonPropertyName("ARK")]
    public SubmarinePairInfo ARK { get; set; }
}

public class SubmarinePairInfo
{
    [JsonPropertyName("BTC")]
    public SubmarinePairDetails BTC { get; set; }
}

public class SubmarinePairDetails
{
    [JsonPropertyName("fees")]
    public FeeInfo Fees { get; set; }
    
    [JsonPropertyName("limits")]
    public LimitsInfo Limits { get; set; }
}

public class FeeInfo
{
    [JsonPropertyName("percentage")]
    public decimal Percentage { get; set; }
    
    // Note: minerFees can be either a number (0) or an object with user/server details
    // For simplicity, we'll accept it as a long when it's just 0
    [JsonPropertyName("minerFees")]
    public long? MinerFeesValue { get; set; }
}

public class MinerFeesInfo
{
    [JsonPropertyName("user")]
    public MinerFeeDetails? User { get; set; }
    
    [JsonPropertyName("server")]
    public MinerFeeDetails? Server { get; set; }
}

public class MinerFeeDetails
{
    [JsonPropertyName("normal")]
    public long Normal { get; set; }
    
    [JsonPropertyName("reverse")]
    public ReverseClaimFees? Reverse { get; set; }
}

public class ReverseClaimFees
{
    [JsonPropertyName("claim")]
    public long Claim { get; set; }
    
    [JsonPropertyName("lockup")]
    public long Lockup { get; set; }
}

public class LimitsInfo
{
    [JsonPropertyName("minimal")]
    public long Minimal { get; set; }
    
    [JsonPropertyName("maximal")]
    public long Maximal { get; set; }
    
    // Note: maximalZeroConf can be either a number (0) or an object with baseAsset/quoteAsset
    // For simplicity, we'll just accept it as a long since the object form is not currently used
    [JsonPropertyName("maximalZeroConf")]
    public long? MaximalZeroConf { get; set; }
}
