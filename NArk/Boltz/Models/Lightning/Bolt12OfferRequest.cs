using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Lightning;

public class Bolt12OfferRequest
{
    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("issuer")]
    public string? Issuer { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("absoluteExpiry")]
    public long? AbsoluteExpiry { get; set; }
}
