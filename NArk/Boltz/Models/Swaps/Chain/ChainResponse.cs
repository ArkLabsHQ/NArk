namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class ChainResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("userLockupDetails")]
    public ChainLockupDetails UserLockupDetails { get; set; }

    [JsonPropertyName("serverLockupDetails")]
    public ChainLockupDetails ServerLockupDetails { get; set; }

    [JsonPropertyName("expectedAmounts")]
    public ExpectedAmounts? ExpectedAmounts { get; set; }
}
