using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Chain;

public class ChainSetTransactionRequest
{
    [JsonPropertyName("hex")]
    public string Hex { get; set; }
}
