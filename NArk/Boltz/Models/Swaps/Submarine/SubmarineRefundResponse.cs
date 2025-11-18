using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Submarine;

/// <summary>
/// Response model for cooperative submarine swap refund.
/// Contains Boltz-signed transaction and checkpoint PSBTs.
/// </summary>
public class SubmarineRefundResponse
{
    /// <summary>
    /// Base64-encoded PSBT of the refund transaction with Boltz's signature.
    /// </summary>
    [JsonPropertyName("transaction")]
    public required string Transaction { get; set; }

    /// <summary>
    /// Base64-encoded PSBT of the checkpoint transaction with Boltz's signature.
    /// </summary>
    [JsonPropertyName("checkpoint")]
    public required string Checkpoint { get; set; }
}
