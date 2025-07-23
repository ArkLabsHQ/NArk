using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Chain;

public class OnchainTransaction
{
    [JsonPropertyName("hex")]
    public string Hex { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }
}
