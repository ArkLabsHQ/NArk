using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Chain;

public class BroadcastRequest
{
    [JsonPropertyName("hex")]
    public string Hex { get; set; }
}
