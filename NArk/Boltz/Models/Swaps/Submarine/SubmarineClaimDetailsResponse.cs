using System.Text.Json.Serialization;
using NArk.Boltz.Models.Swaps.Common;

namespace NArk.Boltz.Models.Swaps.Submarine;

public class SubmarineClaimDetailsResponse
{
    [JsonPropertyName("pubNonce")]
    public string PubNonce { get; set; }

    [JsonPropertyName("partialSignature")]
    public PartialSignature PartialSignature { get; set; }
}
