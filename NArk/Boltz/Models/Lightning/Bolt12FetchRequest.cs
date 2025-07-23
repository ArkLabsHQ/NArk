using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Lightning;

public class Bolt12FetchRequest
{
    [JsonPropertyName("offer")]
    public string Offer { get; set; }

    [JsonPropertyName("amount")]
    public long? Amount { get; set; }

    [JsonPropertyName("payerNote")]
    public string? PayerNote { get; set; }
}
