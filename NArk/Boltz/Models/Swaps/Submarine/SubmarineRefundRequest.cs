using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Submarine;

/// <summary>
/// Request model for cooperative submarine swap refund.
/// Contains unsigned transaction and checkpoint PSBTs for Boltz to co-sign.
/// </summary>
public class SubmarineRefundRequest
{
    /// <summary>
    /// Base64-encoded PSBT of the refund transaction (spending the VHTLC back to sender).
    /// </summary>
    [JsonPropertyName("transaction")]
    public required string Transaction { get; set; }

    /// <summary>
    /// Base64-encoded PSBT of the checkpoint transaction.
    /// </summary>
    [JsonPropertyName("checkpoint")]
    public required string Checkpoint { get; set; }
}
