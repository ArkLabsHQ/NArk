namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class SubmarineClaimDetailsRequest
{
    [JsonPropertyName("transaction")]
    public string Transaction { get; set; } // Transaction hex

    [JsonPropertyName("preimage")]
    public string Preimage { get; set; }

    [JsonPropertyName("pubNonce")]
    public string? PubNonce { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}
