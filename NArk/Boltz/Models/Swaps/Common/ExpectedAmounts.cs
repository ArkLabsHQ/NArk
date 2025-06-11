namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class ExpectedAmounts
{
    [JsonPropertyName("userAmount")]
    public long? UserAmount { get; set; }

    [JsonPropertyName("serverAmount")]
    public long? ServerAmount { get; set; }
}
