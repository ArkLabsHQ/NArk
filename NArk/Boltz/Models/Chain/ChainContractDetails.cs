using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Chain;

public class ChainContractDetails
{
    [JsonPropertyName("address")]
    public string Address { get; set; }

    [JsonPropertyName("abi")]
    public object Abi { get; set; } // Can be complex, represented as object or JsonElement

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; }

    [JsonPropertyName("decimals")]
    public int Decimals { get; set; }

    [JsonPropertyName("isNative")]
    public bool IsNative { get; set; }

    [JsonPropertyName("network")]
    public NetworkDetails Network { get; set; }
}