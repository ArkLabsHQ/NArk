using System.Diagnostics.CodeAnalysis;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using NArk.Services;
using NArk.Wallet.Boltz;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// Handles strings such as "type=ark;wallet-id=WALLETID"
public class ArkLightningConnectionStringHandler(BoltzClient client, IServiceProvider serviceProvider) : ILightningConnectionStringHandler
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
        var dbContextFactory = serviceProvider.GetRequiredService<ArkPluginDbContextFactory>();
        var swapProcessor = serviceProvider.GetRequiredService<LightningSwapProcessor>();
        var walletService = serviceProvider.GetRequiredService<IWalletService>();
        var operatorTermsService = serviceProvider.GetRequiredService<IOperatorTermsService>();
        return new ArkLightningClient(walletId, client, dbContextFactory, swapProcessor, walletService, operatorTermsService);
    }
}

