namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class CurrencyHeightResponse
{
    [JsonPropertyName("height")]
    public long Height { get; set; }
}
