namespace NArk.Wallet.Boltz;

using System.Collections.Generic;
using System.Text.Json.Serialization;

public class ReferralStatsResponse
{
    [JsonPropertyName("totalSwaps")]
    public int TotalSwaps { get; set; }

    [JsonPropertyName("currencies")]
    public Dictionary<string, ReferralCurrencyStats> Currencies { get; set; }
}

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

public class ReferralSwapStats
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("volume")]
    public long Volume { get; set; }
}
