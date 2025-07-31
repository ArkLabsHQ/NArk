using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class VTXOsUpdated
{
    public VTXO[] Vtxos { get; set; }
    
    override public string ToString()
    {
        return null;

    }
}