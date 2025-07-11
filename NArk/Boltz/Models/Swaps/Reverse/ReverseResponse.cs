namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class ReverseResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("invoice")]
    public string Invoice { get; set; }

    [JsonPropertyName("redeemScript")]
    public string? RedeemScript { get; set; }

    [JsonPropertyName("lockupAddress")]
    public string LockupAddress { get; set; }

    [JsonPropertyName("onchainAmount")]
    public long OnchainAmount { get; set; }

    [JsonPropertyName("timeoutBlockHeight")]
    public long TimeoutBlockHeight { get; set; }

    [JsonPropertyName("blindingKey")]
    public string? BlindingKey { get; set; }

    [JsonPropertyName("swapTree")]
    public SwapTree? SwapTree { get; set; }

    [JsonPropertyName("claimAddress")]
    public string? ClaimAddress { get; set; } // For Taproot, user's address

    [JsonPropertyName("refundPublicKey")]
    public string? RefundPublicKey { get; set; } // Boltz's refund public key
}
