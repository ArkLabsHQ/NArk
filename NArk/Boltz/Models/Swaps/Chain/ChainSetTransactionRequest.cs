namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class ChainSetTransactionRequest
{
    [JsonPropertyName("hex")]
    public string Hex { get; set; }
}
