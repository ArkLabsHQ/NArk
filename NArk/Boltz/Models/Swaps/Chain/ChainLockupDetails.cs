namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class ChainLockupDetails
{
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("redeemScript")]
    public string? RedeemScript { get; set; }

    [JsonPropertyName("timeoutBlockHeight")]
    public long? TimeoutBlockHeight { get; set; }

    [JsonPropertyName("blindingKey")]
    public string? BlindingKey { get; set; }

    [JsonPropertyName("expectedAmount")]
    public long? ExpectedAmount { get; set; }

    [JsonPropertyName("swapTree")]
    public SwapTree? SwapTree { get; set; }

    [JsonPropertyName("claimPublicKey")]
    public string? ClaimPublicKey { get; set; }

    [JsonPropertyName("pubNonce")]
    public string? PubNonce { get; set; }

    [JsonPropertyName("partialSignature")]
    public PartialSignature? PartialSignature { get; set; }

    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; } // ID of the lockup tx, if broadcasted

    [JsonPropertyName("transactionHex")]
    public string? TransactionHex { get; set; } // Hex of the lockup tx, if available
}
