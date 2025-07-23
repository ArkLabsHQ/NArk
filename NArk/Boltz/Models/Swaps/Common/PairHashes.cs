using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class PairHashes
{
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; }

    [JsonPropertyName("ripemd160")]
    public string Ripemd160 { get; set; }
}