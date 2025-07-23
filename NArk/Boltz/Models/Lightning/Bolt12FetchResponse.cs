using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Lightning;

public class Bolt12FetchResponse
{
    [JsonPropertyName("invoice")]
    public string Invoice { get; set; }

    [JsonPropertyName("payerNote")]
    public string? PayerNote { get; set; }
}
