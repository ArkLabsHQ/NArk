namespace NArk.Wallet.Boltz;

using System.Collections.Generic;
using System.Text.Json.Serialization;

public class ReferralFeesResponse
{
    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("currencies")]
    public Dictionary<string, ReferralFeeInfo> Currencies { get; set; }
}

public class ReferralFeeInfo
{
    [JsonPropertyName("collected")]
    public long Collected { get; set; }

    [JsonPropertyName("pending")]
    public long Pending { get; set; }
}
