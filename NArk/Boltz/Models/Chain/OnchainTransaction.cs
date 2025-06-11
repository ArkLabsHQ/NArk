namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class OnchainTransaction
{
    [JsonPropertyName("hex")]
    public string Hex { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }
}
