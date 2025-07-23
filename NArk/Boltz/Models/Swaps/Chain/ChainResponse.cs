using System.Text.Json.Serialization;
using NArk.Boltz.Models.Swaps.Common;

namespace NArk.Boltz.Models.Swaps.Chain;

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
