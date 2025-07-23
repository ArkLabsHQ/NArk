using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class ExpectedAmounts
{
    [JsonPropertyName("userAmount")]
    public long? UserAmount { get; set; }

    [JsonPropertyName("serverAmount")]
    public long? ServerAmount { get; set; }
}
