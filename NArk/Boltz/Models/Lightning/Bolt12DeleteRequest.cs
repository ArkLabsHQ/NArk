namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class Bolt12DeleteRequest
{
    [JsonPropertyName("offer")]
    public string Offer { get; set; }
}
