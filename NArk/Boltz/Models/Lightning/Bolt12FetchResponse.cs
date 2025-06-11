namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class Bolt12FetchResponse
{
    [JsonPropertyName("invoice")]
    public string Invoice { get; set; }

    [JsonPropertyName("payerNote")]
    public string? PayerNote { get; set; }
}
