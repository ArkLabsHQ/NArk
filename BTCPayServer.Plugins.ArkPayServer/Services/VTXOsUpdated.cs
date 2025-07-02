using BTCPayServer.Plugins.ArkPayServer.Data.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class VTXOsUpdated
{
    public VTXO[] Vtxos { get; set; }
}