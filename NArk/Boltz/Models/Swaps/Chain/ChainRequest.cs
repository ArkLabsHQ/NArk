namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class ChainRequest
{
    [JsonPropertyName("from")]
    public string From { get; set; } // e.g., "BTC"

    [JsonPropertyName("to")]
    public string To { get; set; } // e.g., "ETH"

    [JsonPropertyName("amount")]
    public long Amount { get; set; } // Amount of the "from" currency

    [JsonPropertyName("userLockupAddress")]
    public string? UserLockupAddress { get; set; } // Optional, Boltz can generate if not provided

    [JsonPropertyName("userRefundAddress")]
    public string UserRefundAddress { get; set; }

    [JsonPropertyName("serverLockupAddress")]
    public string? ServerLockupAddress { get; set; } // Optional, Boltz can generate if not provided

    [JsonPropertyName("serverRefundAddress")]
    public string ServerRefundAddress { get; set; }

    [JsonPropertyName("userPublicKey")]
    public string? UserPublicKey { get; set; } // For Taproot on user side

    [JsonPropertyName("serverPublicKey")]
    public string? ServerPublicKey { get; set; } // For Taproot on server side

    [JsonPropertyName("pairId")]
    public string PairId { get; set; }

    [JsonPropertyName("referralId")]
    public string? ReferralId { get; set; }
}
