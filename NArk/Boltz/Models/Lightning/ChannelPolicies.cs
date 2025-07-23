using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Lightning;

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