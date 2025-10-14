using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NArk;
using NArk.Boltz.Client;
using NArk.Services;
using NBitcoin;
using System.Reflection;
using System.Text.Json;
using BTCPayServer.PayoutProcessors;
using Grpc.Net.ClientFactory;
using NArk.Services.Abstractions;
using BTCPayServer.Plugins.ArkPayServer.Cache;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkadePlugin : BaseBTCPayServerPlugin
{
    internal const string PluginNavKey = nameof(ArkadePlugin) + "Nav";
    internal const string ArkadeDisplayName = "Arkade";

    internal static PaymentMethodId ArkadePaymentMethodId = new PaymentMethodId("ARKADE");
    
    internal static PayoutMethodId ArkadePayoutMethodId = Create();


    private static PayoutMethodId Create()
    {
        //use reflection to access ctor of PayoutMethodId and create it
        var constructor = typeof(PayoutMethodId).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!;
        return (PayoutMethodId) constructor.Invoke(new object[] { "ARKADE" })!;
    }
    
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new () { Identifier = nameof(BTCPayServer), Condition = ">=2.1.0" }
    ];

    public override void Execute(IServiceCollection serviceCollection)
    {
        var pluginServiceCollection = (PluginServiceCollection) serviceCollection;
        
        var (arkUri, boltzUri) = GetServiceUris(pluginServiceCollection);
        
        if (arkUri is null) return;
        
        SetupBtcPayPluginServices(serviceCollection);
        
        serviceCollection.AddSingleton<ArkConfiguration>(_ => new ArkConfiguration(arkUri, boltzUri));
        serviceCollection.AddSingleton<ArkadePaymentMethodHandler>();
        serviceCollection.AddSingleton<ArkPluginDbContextFactory>();
        serviceCollection.AddSingleton<AsyncKeyedLocker>();
        
        serviceCollection.AddSingleton<ArkadeWalletSignerProvider>();
        serviceCollection.AddSingleton<ArkadeContractSweeper>();
        serviceCollection.AddDbContext<ArkPluginDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<ArkPluginDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
        serviceCollection.AddStartupTask<ArkPluginMigrationRunner>();

        serviceCollection.AddSingleton<ArkWalletService>();
        serviceCollection.AddSingleton<ArkadeSpender>();
        serviceCollection.AddSingleton<ArkTransactionBuilder>();
        serviceCollection.AddSingleton<ArkadeCheckoutModelExtension>();
        serviceCollection.AddSingleton<ArkadeCheckoutCheatModeExtension>();
        serviceCollection.AddSingleton<ICheckoutModelExtension>(provider => provider.GetRequiredService<ArkadeCheckoutModelExtension>());
        serviceCollection.AddSingleton<ICheckoutCheatModeExtension>(provider => provider.GetRequiredService<ArkadeCheckoutCheatModeExtension>());
        serviceCollection.AddSingleton<IArkadeMultiWalletSigner>(provider => provider.GetRequiredService<ArkWalletService>());
        serviceCollection.AddSingleton<ArkVtxoSynchronizationService>();
        serviceCollection.AddSingleton<ArkContractInvoiceListener>();
        serviceCollection.AddSingleton<ArkIntentService>();
        serviceCollection.AddSingleton<ArkIntentScheduler>();
        serviceCollection.AddSingleton<BitcoinTimeChainProvider>();
        serviceCollection.AddHostedService<ArkWalletService>(provider => provider.GetRequiredService<ArkWalletService>());
        serviceCollection.AddHostedService<ArkVtxoSynchronizationService>(provider => provider.GetRequiredService<ArkVtxoSynchronizationService>());
        serviceCollection.AddHostedService<ArkContractInvoiceListener>(provider => provider.GetRequiredService<ArkContractInvoiceListener>());
        serviceCollection.AddHostedService<ArkadeContractSweeper>(provider => provider.GetRequiredService<ArkadeContractSweeper>());
        serviceCollection.AddHostedService<ArkIntentService>(provider => provider.GetRequiredService<ArkIntentService>());
        serviceCollection.AddHostedService<ArkIntentScheduler>(provider => provider.GetRequiredService<ArkIntentScheduler>());
        serviceCollection.AddHostedService<BitcoinTimeChainProvider>(provider => provider.GetRequiredService<BitcoinTimeChainProvider>());
        
        serviceCollection.AddSingleton<ArkadeSpendingService>();
        
        // Register the Boltz swap services
        serviceCollection.AddSingleton<BoltzSwapService>();
        serviceCollection.AddSingleton<BoltzService>();
        serviceCollection.AddHostedService<BoltzService>(provider => provider.GetRequiredService<BoltzService>());

        serviceCollection.AddSingleton<TrackedContractsCache>();
        serviceCollection.AddHostedService<TrackedContractsCache>(provider => provider.GetRequiredService<TrackedContractsCache>());

        serviceCollection.AddUIExtension("ln-payment-method-setup-tabhead", "/Views/OldArk/ArkLNSetupTabhead.cshtml");
        serviceCollection.AddUIExtension("dashboard-setup-guide-payment", "/Views/OldArk/DashboardSetupGuidePayment.cshtml");
        serviceCollection.AddUIExtension("store-invoices-payments", "/Views/OldArk/ArkPaymentData.cshtml");
        // Display Ark as a wallet type in navigation sidebar
        serviceCollection.AddUIExtension("store-wallets-nav", "/Views/Ark/ArkWalletNav.cshtml");
        
        // Display ARK instructions in the Lightning setup screen
        serviceCollection.AddUIExtension(
            location: "ln-payment-method-setup-tab",
            partialViewName: "/Views/Lightning/LNPaymentMethodSetupTab.cshtml");
        
        // Use NArk SDK Services
        var configuration = new ArkConfiguration(
            ArkUri: arkUri,
            BoltzUri: boltzUri
        );
        
        serviceCollection.AddGrpcClient<ArkService.ArkServiceClient>(options =>
        {
            options.Address = new Uri(configuration.ArkUri);
            options.InterceptorRegistrations.Add(new InterceptorRegistration(InterceptorScope.Client, provider => new DeadlineInterceptor(TimeSpan.FromSeconds(10))));
            
        });
        
        serviceCollection.AddGrpcClient<IndexerService.IndexerServiceClient>(options =>
        {
            options.Address = new Uri(configuration.ArkUri);
            options.InterceptorRegistrations.Add(new InterceptorRegistration(InterceptorScope.Client, provider => new DeadlineInterceptor(TimeSpan.FromSeconds(10))));

        });

        // Register Ark services
        serviceCollection.AddSingleton<CachedOperatorTermsService>();
        serviceCollection.AddSingleton<IOperatorTermsService, CachedOperatorTermsService>(provider => provider.GetRequiredService<CachedOperatorTermsService>());

        if (!string.IsNullOrWhiteSpace(configuration.BoltzUri))
        {
            serviceCollection.AddHttpClient<BoltzClient>(client =>
            {
                client.BaseAddress = new Uri(configuration.BoltzUri);
            });
        }
    }

    
    private static (string? ArkUri, string? BoltzUri) GetServiceUris(PluginServiceCollection pluginServiceCollection)
    {
        var networkType = 
            DefaultConfiguration.GetNetworkType(
                pluginServiceCollection
                    .BootstrapServices
                    .GetRequiredService<IConfiguration>()
            );
        
        var arkUri = GetArkServiceUri(networkType);
        var boltzUri = GetBoltzServiceUri(networkType);
        
        var configurationServices =
            pluginServiceCollection
                .BootstrapServices
                .GetRequiredService<IConfiguration>();
        
        var arkadeFilePath =
            Path.Combine(new DataDirectories().Configure(configurationServices).DataDir, "ark.json");
        
        if (File.Exists(arkadeFilePath))
        {
            var json = File.ReadAllText(arkadeFilePath);
            var config = JsonSerializer.Deserialize<ArkConfiguration>(json);

            if(!string.IsNullOrEmpty(config?.BoltzUri))
            {
                boltzUri = config.BoltzUri;
            }
            
            if(!string.IsNullOrEmpty(config?.ArkUri))
            {
                arkUri = config.ArkUri;
            }
        }

        return (arkUri, boltzUri);
    }

    private static string? GetArkServiceUri(ChainName networkType)
    {
        if (networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName)
            return "https://mutinynet.arkade.sh";
        if (networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName)
            return "https://signet.arkade.sh";
        if (networkType == ChainName.Regtest)
            return "http://localhost:7070";
        return null;
    }
    
    private static string? GetBoltzServiceUri(ChainName networkType)
    {
        if (networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName)
            return "https://mutinynet.boltz.exchange/";
        if (networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName)
            return "https://signet.boltz.exchange/";
        if (networkType == ChainName.Regtest)
            return "http://localhost:9001/";
        return null;
    }

    private static void SetupBtcPayPluginServices(IServiceCollection serviceCollection)
    {
        // Register ArkConnectionStringHandler so LightningClientFactoryService can create the client
        serviceCollection.AddSingleton<ILightningConnectionStringHandler, ArkLightningConnectionStringHandler>();
        serviceCollection.AddSingleton<ArkadePaymentLinkExtension>();
        serviceCollection.AddSingleton<IPaymentLinkExtension>(provider => provider.GetRequiredService<ArkadePaymentLinkExtension>());
        serviceCollection.AddSingleton<IPaymentMethodHandler>(provider => provider.GetRequiredService<ArkadePaymentMethodHandler>());
        serviceCollection.AddSingleton<ArkPayoutHandler>();
        serviceCollection.AddSingleton<IPayoutHandler>(provider => provider.GetRequiredService<ArkPayoutHandler>());
        serviceCollection.AddSingleton<ArkAutomatedPayoutSenderFactory>();
        serviceCollection.AddSingleton<IPayoutProcessorFactory>(provider => provider.GetRequiredService<ArkAutomatedPayoutSenderFactory>());
        
        serviceCollection.AddDefaultPrettyName(ArkadePaymentMethodId, "Arkade");
    }
}