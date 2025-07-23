using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Lightning;

public class Bolt12DeleteRequest
{
    [JsonPropertyName("offer")]
    public string Offer { get; set; }
}
