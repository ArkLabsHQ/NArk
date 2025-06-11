namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

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

public class NetworkDetails
{
    [JsonPropertyName("chainId")]
    public long ChainId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("explorerUrl")]
    public string ExplorerUrl { get; set; }
}
