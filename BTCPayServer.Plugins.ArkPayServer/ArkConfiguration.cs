using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.ArkPayServer;

public record ArkConfiguration(
    [property: JsonPropertyName("ark")] string ArkUri,
    [property: JsonPropertyName("boltz")] string? BoltzUri);