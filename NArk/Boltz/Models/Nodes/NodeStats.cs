namespace NArk.Wallet.Boltz;

using System.Collections.Generic;
using System.Text.Json.Serialization;

public class NodeStats
{
    [JsonPropertyName("nodes")]
    public Dictionary<string, NodeInfo> Nodes { get; set; }
}

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

public class NodeChannels
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("active")]
    public int Active { get; set; }

    [JsonPropertyName("inactive")]
    public int Inactive { get; set; }

    [JsonPropertyName("pending")]
    public int Pending { get; set; }

    [JsonPropertyName("closed")]
    public int Closed { get; set; }
}
