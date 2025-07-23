using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Nodes;

public class NodeInfo
{
    [JsonPropertyName("uris")]
    public List<string> Uris { get; set; }

    [JsonPropertyName("alias")]
    public string Alias { get; set; }

    [JsonPropertyName("color")]
    public string Color { get; set; }

    [JsonPropertyName("channels")]
    public NodeChannels Channels { get; set; }

    [JsonPropertyName("blockHeight")]
    public long BlockHeight { get; set; }

    [JsonPropertyName("syncedToChain")]
    public bool SyncedToChain { get; set; }

    [JsonPropertyName("syncedToGraph")]
    public bool SyncedToGraph { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }
}