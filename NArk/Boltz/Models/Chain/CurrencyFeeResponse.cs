namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class CurrencyFeeResponse
{
    [JsonPropertyName("fee")]
    public double Fee { get; set; }
}
