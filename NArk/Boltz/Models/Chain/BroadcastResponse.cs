using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Chain;

public class BroadcastResponse
{
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; }
}
