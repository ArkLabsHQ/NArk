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
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.ClientFactory;

namespace BTCPayServer.Plugins.ArkPayServer;

class DeadlineInterceptor(TimeSpan deadline) : Interceptor
{
    private void ApplyDeadline<TRequest, TResponse>(ref ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        if (context.Options.Deadline is null)
        {
            context = new(context.Method, context.Host, context.Options.WithDeadline(DateTime.UtcNow.Add(deadline)));
        }
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ApplyDeadline(ref context);
        return base.AsyncClientStreamingCall(context, continuation);
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context, AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ApplyDeadline(ref context);
        return base.AsyncDuplexStreamingCall(context, continuation);
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ApplyDeadline(ref context);
        return base.AsyncServerStreamingCall(request, context, continuation);
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ApplyDeadline(ref context);
        return base.AsyncUnaryCall(request, context, continuation);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ApplyDeadline(ref context);
        return base.BlockingUnaryCall(request, context, continuation);
    }

    // note no need to intercept server methods
}
public class ArkadePlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = nameof(ArkadePlugin) + "Nav";

    internal static PaymentMethodId ArkadePaymentMethodId = new PaymentMethodId("ARKADE");
    internal static PayoutMethodId ArkadePayoutMethodId = Create();
    internal static string ArkadeDisplayName = "Arkade";


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
        var configurationServices = pluginServiceCollection.BootstrapServices.GetRequiredService<IConfiguration>();
        
        var arkadeFilePath = Path.Combine(new DataDirectories().Configure(configurationServices).DataDir, "ark.json");

     

        var networkType  = DefaultConfiguration.GetNetworkType(pluginServiceCollection.BootstrapServices.GetRequiredService<IConfiguration>());

        var arkUri = networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName
            ? "https://mutinynet.arkade.sh"
            : networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName
                ? "https://signet.arkade.sh"
                : networkType == ChainName.Regtest
                    ? "http://localhost:7070"
                    : null;

       
        
        var boltzUri = networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName
            ? "https://mutinynet.boltz.exchange/"
            : networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName
                ? "https://signet.boltz.exchange/"
                : networkType == ChainName.Regtest
                    ? "http://localhost:9001/"
                    : null;
        
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
        if (arkUri is null)
        {
            return;
        }
        
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
        serviceCollection.AddSingleton<ArkSubscriptionService>();
        serviceCollection.AddSingleton<ArkContractInvoiceListener>();
        serviceCollection.AddHostedService<ArkWalletService>(provider => provider.GetRequiredService<ArkWalletService>());
        serviceCollection.AddHostedService<ArkSubscriptionService>(provider => provider.GetRequiredService<ArkSubscriptionService>());
        serviceCollection.AddHostedService<ArkContractInvoiceListener>(provider => provider.GetRequiredService<ArkContractInvoiceListener>());
        serviceCollection.AddHostedService<ArkadeContractSweeper>(provider => provider.GetRequiredService<ArkadeContractSweeper>());
        
        // Register the Boltz swap services
        serviceCollection.AddSingleton<BoltzSwapService>();
        serviceCollection.AddSingleton<BoltzService>();
        serviceCollection.AddHostedService<BoltzService>(provider => provider.GetRequiredService<BoltzService>());

        
        serviceCollection.AddUIExtension("ln-payment-method-setup-tabhead", "/Views/Ark/ArkLNSetupTabhead.cshtml");
        serviceCollection.AddUIExtension("store-invoices-payments", "/Views/Ark/ArkPaymentData.cshtml");
        // Display Ark as a wallet type in navigation sidebar
        serviceCollection.AddUIExtension("store-wallets-nav", "/Views/Ark/ArkWalletNav.cshtml");
        
        // Display ARK instructions in the Lightning setup screen
        serviceCollection.AddUIExtension(
            location: "ln-payment-method-setup-tab",
            partialViewName: "/Views/Lightning/LNPaymentMethodSetupTab.cshtml");
        
        // Use NArk SDK Services
        var configuration = new ArkConfiguration(
            arkUri: arkUri,
            boltzUri: boltzUri
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
    
    private static void SetupBtcPayPluginServices(IServiceCollection serviceCollection)
    {
        // Register ArkConnectionStringHandler so LightningClientFactoryService can create the client
        serviceCollection.AddSingleton<ILightningConnectionStringHandler, ArkLightningConnectionStringHandler>();
        serviceCollection.AddSingleton<ArkadePaymentLinkExtension>();
        serviceCollection.AddSingleton<IPaymentLinkExtension>(provider => provider.GetRequiredService<ArkadePaymentLinkExtension>());
        serviceCollection.AddSingleton<IPaymentMethodHandler>(provider => provider.GetRequiredService<ArkadePaymentMethodHandler>());
        
        serviceCollection.AddDefaultPrettyName(ArkadePaymentMethodId, "Arkade");
    }
    
    public override void Execute(IApplicationBuilder applicationBuilder, IServiceProvider provider)
    {
        base.Execute(applicationBuilder, provider);
    }
}