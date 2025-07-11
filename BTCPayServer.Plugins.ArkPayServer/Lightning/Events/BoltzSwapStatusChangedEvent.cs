using BTCPayServer.Plugins.ArkPayServer.Data.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning.Events;

public record BoltzSwapStatusChangedEvent(string SwapId, string Status, bool Active, string? Script, string? WalletId)
{
}