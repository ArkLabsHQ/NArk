using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Referral;

public class ReferralCurrencyStats
{
    [JsonPropertyName("totalSwaps")]
    public int TotalSwaps { get; set; }

    [JsonPropertyName("submarine")]
    public ReferralSwapStats Submarine { get; set; }

    [JsonPropertyName("reverse")]
    public ReferralSwapStats Reverse { get; set; }

    [JsonPropertyName("chain")]
    public ReferralSwapStats Chain { get; set; }
}