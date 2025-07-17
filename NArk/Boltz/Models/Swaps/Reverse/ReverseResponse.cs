namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class ReverseResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; }
    
    [JsonPropertyName("lockupAddress")]
    public string LockupAddress { get; set; }
    
    [JsonPropertyName("refundPublicKey")]
    public string RefundPublicKey { get; set; }
    
    [JsonPropertyName("timeoutBlockHeights")]
    public TimeoutBlockHeights TimeoutBlockHeights { get; set; }
    
    [JsonPropertyName("invoice")]
    public string Invoice { get; set; }
    
    [JsonPropertyName("onchainAmount")]
    public int OnchainAmount { get; set; }

 
}

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


