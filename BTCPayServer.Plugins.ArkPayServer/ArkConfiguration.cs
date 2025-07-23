namespace BTCPayServer.Plugins.ArkPayServer;

public record ArkConfiguration(
    string ArkUri,
    string? BoltzUri
);