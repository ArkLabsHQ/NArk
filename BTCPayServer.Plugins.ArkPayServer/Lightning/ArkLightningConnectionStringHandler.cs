using System.Diagnostics.CodeAnalysis;
using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkLightningConnectionStringHandler: ILightningConnectionStringHandler
{
    public ILightningClient Create(string connectionString, Network network, [UnscopedRef] out string error)
    {
        throw new NotImplementedException();
    }
}

