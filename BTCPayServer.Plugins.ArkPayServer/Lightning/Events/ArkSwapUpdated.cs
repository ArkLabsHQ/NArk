using BTCPayServer.Plugins.ArkPayServer.Data.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning.Events;

public record ArkSwapUpdated
{

    public override string ToString()
    {
        return $"Ark Swap:{Swap.SwapId} {Swap.Status}";
    }

    public ArkSwap Swap { get; init; }

    public VTXO[] Vtxos { get; set; }
}