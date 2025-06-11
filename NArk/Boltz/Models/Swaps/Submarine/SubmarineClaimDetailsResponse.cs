namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class SubmarineClaimDetailsResponse
{
    [JsonPropertyName("pubNonce")]
    public string PubNonce { get; set; }

    [JsonPropertyName("partialSignature")]
    public PartialSignature PartialSignature { get; set; }
}
