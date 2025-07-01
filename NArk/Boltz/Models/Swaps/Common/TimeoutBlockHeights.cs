namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class TimeoutBlockHeights
{
    [JsonPropertyName("unilateralClaim")]
    public long UnilateralClaim { get; set; }

    [JsonPropertyName("unilateralRefund")]
    public long UnilateralRefund { get; set; }

    [JsonPropertyName("unilateralRefundWithoutReceiver")]
    public long UnilateralRefundWithoutReceiver { get; set; }
}