using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Lightning;

public class Bolt12OfferResponse
{
    [JsonPropertyName("offer")]
    public string Offer { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("singleUse")]
    public bool SingleUse { get; set; }

    [JsonPropertyName("bolt12")]
    public string Bolt12 { get; set; }

    [JsonPropertyName("used")]
    public bool Used { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } // Consider DateTimeOffset if parsing

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } // Consider DateTimeOffset if parsing
}
