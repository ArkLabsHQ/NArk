namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class ClaimDetails
{
    [JsonPropertyName("outputDescriptor")]
    public string OutputDescriptor { get; set; }

    [JsonPropertyName("script")]
    public string Script { get; set; }
}
