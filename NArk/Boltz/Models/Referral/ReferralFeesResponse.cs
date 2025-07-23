using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Referral;

public class ReferralFeesResponse
{
    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("currencies")]
    public Dictionary<string, ReferralFeeInfo> Currencies { get; set; }
}