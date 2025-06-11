namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class BroadcastRequest
{
    [JsonPropertyName("hex")]
    public string Hex { get; set; }
}
