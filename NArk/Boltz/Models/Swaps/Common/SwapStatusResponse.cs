using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class SwapStatusResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("zeroConfRejected")]
    public bool? ZeroConfRejected { get; set; }

    [JsonPropertyName("transaction")]
    public SwapTransaction? Transaction { get; set; }

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    [JsonPropertyName("failureDetails")]
    public SwapFailureDetails? FailureDetails { get; set; }

    [JsonPropertyName("expectedAmounts")]
    public SwapExpectedAmounts? ExpectedAmounts { get; set; }

    [JsonPropertyName("invoice")]
    public string? Invoice { get; set; } // For Submarine Swaps

    [JsonPropertyName("lockupAddress")]
    public string? LockupAddress { get; set; } // For Reverse Swaps

    [JsonPropertyName("lockupTransactionId")]
    public string? LockupTransactionId { get; set; } // For Reverse Swaps, if server locked up

    [JsonPropertyName("claimAddress")]
    public string? ClaimAddress { get; set; } // For Reverse Taproot Swaps

    [JsonPropertyName("claimTransactionId")]
    public string? ClaimTransactionId { get; set; } // For Submarine Swaps, if claimed by Boltz

    [JsonPropertyName("refundAddress")]
    public string? RefundAddress { get; set; } // For Submarine Swaps, if user can refund

    [JsonPropertyName("refundTransactionId")]
    public string? RefundTransactionId { get; set; } // For Reverse Swaps, if refunded by Boltz

    [JsonPropertyName("blindingKey")]
    public string? BlindingKey { get; set; }

    [JsonPropertyName("redeemScript")]
    public string? RedeemScript { get; set; }

    [JsonPropertyName("timeoutBlockHeight")]
    public long? TimeoutBlockHeight { get; set; }

    [JsonPropertyName("lockTime")]
    public long? LockTime { get; set; } // For Chain Swaps

    [JsonPropertyName("userLockupAddress")]
    public string? UserLockupAddress { get; set; } // For Chain Swaps

    [JsonPropertyName("serverLockupAddress")]
    public string? ServerLockupAddress { get; set; } // For Chain Swaps
}