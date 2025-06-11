using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using AsyncKeyedLock;
using NArk;

namespace BTCPayServer.Plugins.ArkPayServer;




public static class AppExtensions
{
    public static IServiceCollection AddArkPayServer(this IServiceCollection serviceCollection)
    {
        var pluginServiceCollection = (PluginServiceCollection) serviceCollection;
        var networkType  = DefaultConfiguration.GetNetworkType(pluginServiceCollection.BootstrapServices.GetRequiredService<IConfiguration>());

        var arkUri = networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName
            ? "htps://mutinynet.arkade.sh"
            : networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName
                ? "https://signet.arkade.sh"
                : networkType == ChainName.Regtest
                    ? "https://localhost:3000"
                    : null;
        
        if (arkUri is null)
        {
            return serviceCollection; 
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
        serviceCollection.AddGrpcClient<Ark.V1.ArkService.ArkServiceClient>(
            options =>
            {
                options.Address = new Uri(arkUri);
            });
        serviceCollection.AddGrpcClient<Ark.V1.IndexerService.IndexerServiceClient>(
            options =>
            {
                options.Address = new Uri(arkUri);
            });
        serviceCollection.AddHostedService<ArkService>();
        return serviceCollection;
    }

}