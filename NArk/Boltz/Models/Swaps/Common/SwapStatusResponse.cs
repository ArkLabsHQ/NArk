namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

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

public class SwapTransaction
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("hex")]
    public string? Hex { get; set; }

    [JsonPropertyName("eta")]
    public string? Eta { get; set; } // Could be string like "pending" or a timestamp/blockheight
}

public class SwapFailureDetails
{
    [JsonPropertyName("onchain")]
    public string? Onchain { get; set; }

    [JsonPropertyName("offchain")]
    public string? Offchain { get; set; }
}

public class SwapExpectedAmounts
{
    [JsonPropertyName("invoiceAmount")]
    public long? InvoiceAmount { get; set; }

    [JsonPropertyName("onchainAmount")]
    public long? OnchainAmount { get; set; }

    [JsonPropertyName("userSendAmount")]
    public long? UserSendAmount { get; set; } // For Chain Swaps

    [JsonPropertyName("serverSendAmount")]
    public long? ServerSendAmount { get; set; } // For Chain Swaps
}
