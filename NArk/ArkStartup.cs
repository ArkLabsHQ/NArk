using Ark.V1;
using Microsoft.Extensions.DependencyInjection;
using NArk.Services;
using NArk.Wallet.Boltz;
using WalletService = NArk.Services.WalletService;

namespace NArk;

public static class ArkStartup
{
    /// <summary>
    /// This method lets you register the Ark related services in this SDK to your service collection.
    /// These services will provide you a high level API, solving common use cases of an Ark client.
    ///
    /// NOTE: You do not necessarily need to call this method in order to make use of this library.
    /// You can still use the defined primitives, even if you do not call this method. That might be useful
    /// if you want to build a custom flow which does not match what the services registered by this method offer.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static IServiceCollection AddArkServices(this IServiceCollection services, ArkConfiguration configuration)
    {
        // Register gRPC clients
        services.AddGrpcClient<ArkService.ArkServiceClient>(options =>
        {
            options.Address = new Uri(configuration.ArkUri);
        });
        
        services.AddGrpcClient<IndexerService.IndexerServiceClient>(options =>
        {
            options.Address = new Uri(configuration.ArkUri);
        });

        // Register Ark services
        services.AddSingleton<IOperatorTermsService, OperatorTermsService>();
        services.AddTransient<IWalletService, WalletService>();

        if (!string.IsNullOrWhiteSpace(configuration.BoltzUri))
        {
            RegisterBoltz(services, configuration.BoltzUri);
        }
        
        return services;
    }

    private static void RegisterBoltz(IServiceCollection services, string boltzUri)
    {
        services.AddHttpClient<BoltzClient>(client =>
        {
            client.BaseAddress = new Uri(boltzUri);
        });
    }
}

public record ArkConfiguration(
    string ArkUri,
    string? BoltzUri
    );