using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NArk;
using NArk.Wallet.Boltz;
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
        
        var boltzUri = networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName
            ? "https://mutinynet.boltz.exchange"
            : networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName
                ? "https://signet.boltz.exchange"
                : networkType == ChainName.Regtest
                    ? "http://localhost:9001/v2/"
                    : null;
        
        SetupBtcPayPluginServices(serviceCollection);
        
        serviceCollection.AddSingleton<ArkadePaymentMethodHandler>();
        serviceCollection.AddSingleton<ArkPluginDbContextFactory>();
        serviceCollection.AddSingleton<AsyncKeyedLocker>();
        
        serviceCollection.AddSingleton<ArkadeWalletSignerProvider>();
        serviceCollection.AddSingleton<ArkadeTweakedContractSweeper>();
        serviceCollection.AddSingleton<ArkadeHTLCContractSweeper>();
        serviceCollection.AddDbContext<ArkPluginDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<ArkPluginDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
        serviceCollection.AddStartupTask<ArkPluginMigrationRunner>();

        serviceCollection.AddSingleton<ArkWalletService>();
        serviceCollection.AddSingleton<ArkadeCheckoutModelExtension>();
        serviceCollection.AddSingleton<ICheckoutModelExtension>(provider => provider.GetRequiredService<ArkadeCheckoutModelExtension>());
        serviceCollection.AddSingleton<IArkadeMultiWalletSigner>(provider => provider.GetRequiredService<ArkWalletService>());
        serviceCollection.AddSingleton<ArkSubscriptionService>();
        serviceCollection.AddSingleton<ArkContractInvoiceListener>();
        serviceCollection.AddHostedService<ArkSubscriptionService>(provider => provider.GetRequiredService<ArkSubscriptionService>());
        serviceCollection.AddHostedService<ArkadeTweakedContractSweeper>(provider => provider.GetRequiredService<ArkadeTweakedContractSweeper>());
        serviceCollection.AddHostedService<ArkContractInvoiceListener>(provider => provider.GetRequiredService<ArkContractInvoiceListener>());
        serviceCollection.AddHostedService<ArkadeHTLCContractSweeper>(provider => provider.GetRequiredService<ArkadeHTLCContractSweeper>());

        serviceCollection.AddUIExtension("store-invoices-payments", "/Views/Ark/ArkPaymentData.cshtml");
        // Display Ark as a wallet type in navigation sidebar
        serviceCollection.AddUIExtension("store-wallets-nav", "/Views/Ark/ArkWalletNav.cshtml");
        
        // Display ARK instructions in the Lightning setup screen
        serviceCollection.AddUIExtension(
            location: "ln-payment-method-setup-tab",
            partialViewName: "/Views/Lightning/LNPaymentMethodSetupTab.cshtml");
        
        // Use NArk SDK Services
        serviceCollection.AddArkServices(new ArkConfiguration(
            ArkUri: arkUri,
            BoltzUri: boltzUri
            ));
    }
    
    private static void SetupBtcPayPluginServices(IServiceCollection serviceCollection)
    {
        // Register ArkConnectionStringHandler so LightningClientFactoryService can create the client
        serviceCollection.AddSingleton<ILightningConnectionStringHandler, ArkLightningConnectionStringHandler>();
        serviceCollection.AddSingleton<ArkadePaymentLinkExtension>();
        serviceCollection.AddSingleton<IPaymentLinkExtension>(provider => provider.GetRequiredService<ArkadePaymentLinkExtension>());
        serviceCollection.AddSingleton<IPaymentMethodHandler>(provider => provider.GetRequiredService<ArkadePaymentMethodHandler>());
        
        // Register the Boltz swap monitoring hosted service
        serviceCollection.AddSingleton<BoltzSwapMonitorService>();
        serviceCollection.AddHostedService<BoltzSwapMonitorService>(provider => provider.GetRequiredService<BoltzSwapMonitorService>());
        
        serviceCollection.AddDefaultPrettyName(ArkadePaymentMethodId, "Arkade");
    }
    
    public override void Execute(IApplicationBuilder applicationBuilder, IServiceProvider provider)
    {
        base.Execute(applicationBuilder, provider);
    }
}





