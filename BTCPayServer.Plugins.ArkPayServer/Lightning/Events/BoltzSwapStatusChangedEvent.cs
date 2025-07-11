namespace BTCPayServer.Plugins.ArkPayServer.Lightning.Events;

public record BoltzSwapStatusChangedEvent(string SwapId, string Status);