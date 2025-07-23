using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Chain;

public class CurrencyFeeResponse
{
    [JsonPropertyName("fee")]
    public double Fee { get; set; }
}
