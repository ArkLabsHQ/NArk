namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class ChannelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("capacity")]
    public long Capacity { get; set; }

    [JsonPropertyName("localBalance")]
    public long LocalBalance { get; set; }

    [JsonPropertyName("remoteBalance")]
    public long RemoteBalance { get; set; }

    [JsonPropertyName("policies")]
    public ChannelPolicies Policies { get; set; }

    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }

    [JsonPropertyName("remotePublicKey")]
    public string RemotePublicKey { get; set; }

    [JsonPropertyName("shortId")]
    public string ShortId { get; set; }

    [JsonPropertyName("timeouts")]
    public ChannelTimeouts Timeouts { get; set; }
}

public class ChannelPolicies
{
    [JsonPropertyName("baseFeeMsat")]
    public long BaseFeeMsat { get; set; }

    [JsonPropertyName("feeRate")]
    public long FeeRate { get; set; }

    [JsonPropertyName("maxHtlcMsat")]
    public long MaxHtlcMsat { get; set; }

    [JsonPropertyName("minHtlcMsat")]
    public long MinHtlcMsat { get; set; }
}

public class ChannelTimeouts
{
    [JsonPropertyName("csvTimeout")]
    public int CsvTimeout { get; set; }
}
