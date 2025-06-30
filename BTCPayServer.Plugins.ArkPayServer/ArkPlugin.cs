using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer;


public class ArkadePlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = nameof(ArkadePlugin) + "Nav";

    internal static PaymentMethodId ArkadePaymentMethodId = new PaymentMethodId("ARKADE");
    internal static string ArkadeDisplayName = "Arkade";
    
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new () { Identifier = nameof(BTCPayServer), Condition = ">=2.1.0" }
    ];

    public override void Execute(IServiceCollection serviceCollection)
    {
        var pluginServiceCollection = (PluginServiceCollection) serviceCollection;
        var networkType  = DefaultConfiguration.GetNetworkType(pluginServiceCollection.BootstrapServices.GetRequiredService<IConfiguration>());

        var arkUri = networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName
            ? "https://mutinynet.arkade.sh"
            : networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName
                ? "https://signet.arkade.sh"
                : networkType == ChainName.Regtest
                    ? "http://localhost:7070"
                    : null;

        if (arkUri is null)
        {
            return;
        }
        //
        // var boltzUri = networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName
        //     ? "https://mutinynet.boltz.exchange"
        //     : networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName
        //         ? "https://signet.boltz.exchange"
        //         : networkType == ChainName.Regtest
        //             ? "https://localhost:3001"
        //             : null;
        //
        //
        // serviceCollection.AddSingleton<ILightningConnectionStringHandler, ArkLightningConnectionStringHandler>();
        serviceCollection.AddSingleton<ArkPluginDbContextFactory>();
        serviceCollection.AddSingleton<AsyncKeyedLocker>();
        serviceCollection.AddDbContext<ArkPluginDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<ArkPluginDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
        serviceCollection.AddStartupTask<ArkPluginMigrationRunner>();
        serviceCollection.AddGrpcClient<ArkService.ArkServiceClient>(
            options =>
            {
                options.Address = new Uri(arkUri);
            });
        serviceCollection.AddGrpcClient<IndexerService.IndexerServiceClient>(
            options =>
            {
                options.Address = new Uri(arkUri);
            });
        serviceCollection.AddSingleton<ArkOperatorTermsService>();
        serviceCollection.AddTransient<ArkWalletService>();
        serviceCollection.AddSingleton<ArkSubscriptionService>();
        serviceCollection.AddHostedService<ArkSubscriptionService>(provider => provider.GetRequiredService<ArkSubscriptionService>());
        
        serviceCollection.AddUIExtension("store-wallets-nav", "/Views/Ark/ArkWalletNav.cshtml");

         }

    public override void Execute(IApplicationBuilder applicationBuilder, IServiceProvider provider)
    {
        base.Execute(applicationBuilder, provider);
    }
}





