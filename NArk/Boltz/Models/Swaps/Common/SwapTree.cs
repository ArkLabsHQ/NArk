using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class SwapTree
{
    [JsonPropertyName("claimLeaf")]
    public ClaimDetails ClaimLeaf { get; set; }

    [JsonPropertyName("refundLeaf")]
    public ClaimDetails RefundLeaf { get; set; }
}
