using System.Text.Json.Nodes;

namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class WebSocketResponse
{
    [JsonPropertyName("event")]
    public string Event { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; }

    [JsonPropertyName("args")]
    public JsonArray Args { get; set; }
}
