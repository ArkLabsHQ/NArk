using System.Diagnostics.CodeAnalysis;
using BTCPayServer.Lightning;
using NArk.Wallet.Boltz;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// Handles strings such as "type=ark;wallet-id=WALLETID"
public class ArkLightningConnectionStringHandler(BoltzClient client) : ILightningConnectionStringHandler
{
    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "ark")
        {
            error = "The key 'type' must be set to 'ark' for ArkLightning connection strings";
            return null;
        }

        if (!kv.TryGetValue("walletid", out var walletId))
        {
            error = "The key 'walletid' is mandatory for ArkLightning connection strings";
            return null;
        }

        error = null;
        return new ArkLightningClient(walletId, client);
    }
}

