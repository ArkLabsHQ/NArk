using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Reverse;

public class TimeoutBlockHeights
{
    [JsonPropertyName("refund")]
    public int Refund { get; set; }
    
    [JsonPropertyName("unilateralClaim")]
    public int UnilateralClaim { get; set; }
    
    [JsonPropertyName("unilateralRefund")]
    public int UnilateralRefund { get; set; }
    
    [JsonPropertyName("unilateralRefundWithoutReceiver")]
    public int UnilateralRefundWithoutReceiver { get; set; }
}