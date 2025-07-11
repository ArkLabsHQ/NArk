using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using NArk.Services;
using NArk.Wallet.Boltz;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkLightningConnectionStringHandler(IServiceProvider serviceProvider) : ILightningConnectionStringHandler
{
    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "arkade")
        {
            error = "The key 'type' must be set to 'arkade' for ArkLightning connection strings";
            return null;
        }

        if (!kv.TryGetValue("wallet-id", out var walletId))
        {
            error = "The key 'wallet-id' is mandatory for ArkLightning connection strings";
            return null;
        }

        error = null;
        var boltzClient = serviceProvider.GetRequiredService<BoltzClient>();
        var dbContextFactory = serviceProvider.GetRequiredService<ArkPluginDbContextFactory>();
        var walletService = serviceProvider.GetRequiredService<ArkWalletService>();
        var operatorTermsService = serviceProvider.GetRequiredService<IOperatorTermsService>();
        var boltzSwapMonitorService = serviceProvider.GetRequiredService<BoltzSwapSubscriptionService>();
        return new ArkLightningClient(network, walletId, boltzClient, dbContextFactory, walletService, operatorTermsService, boltzSwapMonitorService, serviceProvider);
    }
}

