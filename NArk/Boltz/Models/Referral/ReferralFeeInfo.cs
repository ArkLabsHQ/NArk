using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Referral;

public class ReferralFeeInfo
{
    [JsonPropertyName("collected")]
    public long Collected { get; set; }

    [JsonPropertyName("pending")]
    public long Pending { get; set; }
}