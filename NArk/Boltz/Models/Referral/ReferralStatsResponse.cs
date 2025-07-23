using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Referral;

public class ReferralStatsResponse
{
    [JsonPropertyName("totalSwaps")]
    public int TotalSwaps { get; set; }

    [JsonPropertyName("currencies")]
    public Dictionary<string, ReferralCurrencyStats> Currencies { get; set; }
}