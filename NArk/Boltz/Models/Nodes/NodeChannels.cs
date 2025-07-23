using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Nodes;

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