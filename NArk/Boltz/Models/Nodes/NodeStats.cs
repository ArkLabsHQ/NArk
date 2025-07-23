using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Nodes;

public class NodeStats
{
    [JsonPropertyName("nodes")]
    public Dictionary<string, NodeInfo> Nodes { get; set; }
}