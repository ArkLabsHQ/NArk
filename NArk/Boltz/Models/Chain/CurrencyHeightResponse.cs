using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Chain;

public class CurrencyHeightResponse
{
    [JsonPropertyName("height")]
    public long Height { get; set; }
}
