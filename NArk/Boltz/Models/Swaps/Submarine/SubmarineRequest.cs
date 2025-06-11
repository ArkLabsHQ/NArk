namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class SubmarineRequest
{
    [JsonPropertyName("from")]
    public string From { get; set; } // e.g., "BTC"

    [JsonPropertyName("to")]
    public string To { get; set; } // e.g., "LNBTC"

    [JsonPropertyName("invoice")]
    public string Invoice { get; set; }

    [JsonPropertyName("refundPublicKey")]
    public string RefundPublicKey { get; set; }

    [JsonPropertyName("refundAddress")]
    public string? RefundAddress { get; set; } // Optional, for Liquid

    [JsonPropertyName("claimPublicKey")]
    public string? ClaimPublicKey { get; set; } // Optional, for Taproot

    [JsonPropertyName("pairId")]
    public string PairId { get; set; }

    [JsonPropertyName("channelDetails")]
    public ChannelCreationInfo? ChannelDetails { get; set; }

    [JsonPropertyName("referralId")]
    public string? ReferralId { get; set; }
}

public class ChannelCreationInfo
{
    [JsonPropertyName("auto")]
    public bool Auto { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("inboundLiquidity")]
    public long? InboundLiquidity { get; set; }
}
