using System.Text.Json.Nodes;

namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class WebSocketRequest
{
    [JsonPropertyName("op")]
    public string Operation { get; set; } // e.g., "subscribe", "unsubscribe"

    [JsonPropertyName("channel")]
    public string Channel { get; set; } // e.g., "swap.update"

    [JsonPropertyName("args")]
    public JsonArray Args { get; set; } // e.g., array of swap IDs
}
