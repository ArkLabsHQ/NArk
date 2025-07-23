using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Referral;

public class ReferralSwapStats
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("volume")]
    public long Volume { get; set; }
}