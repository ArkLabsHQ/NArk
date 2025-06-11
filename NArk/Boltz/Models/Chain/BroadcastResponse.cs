namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class BroadcastResponse
{
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; }
}
