using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.WebSocket;

public class WebSocketRequest
{
    [JsonPropertyName("op")]
    public string Operation { get; set; } // e.g., "subscribe", "unsubscribe"

    [JsonPropertyName("channel")]
    public string Channel { get; set; } // e.g., "swap.update"

    [JsonPropertyName("args")]
    public JsonArray Args { get; set; } // e.g., array of swap IDs
}
