namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class SwapTree
{
    [JsonPropertyName("claimLeaf")]
    public ClaimDetails ClaimLeaf { get; set; }

    [JsonPropertyName("refundLeaf")]
    public ClaimDetails RefundLeaf { get; set; }
}
