namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class PartialSignature
{
    [JsonPropertyName("r")]
    public string R { get; set; }

    [JsonPropertyName("s")]
    public string S { get; set; }

    [JsonPropertyName("v")]
    public int V { get; set; }
}
