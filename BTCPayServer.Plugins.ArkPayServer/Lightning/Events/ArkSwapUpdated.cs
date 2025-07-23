using BTCPayServer.Plugins.ArkPayServer.Data.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning.Events;

public record ArkSwapUpdated(ArkSwap Swap)
{

    public override string ToString()
    {
        return $"Ark Swap:{Swap.SwapId} {Swap.Status}";
    }
}