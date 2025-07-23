using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Chain;

public class NetworkDetails
{
    [JsonPropertyName("chainId")]
    public long ChainId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("explorerUrl")]
    public string ExplorerUrl { get; set; }
}