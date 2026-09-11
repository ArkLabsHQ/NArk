using BTCPayServer;
using BTCPayServer.Client.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionLightningPromptTests
{
    private static readonly PaymentMethodId Lightning = PaymentTypes.LN.GetPaymentMethodId("BTC");

    [Fact]
    public async Task RegisteredDecoratorKeepsConcreteLightningSettingsPathOperational()
    {
        await using var database = await CompositionDatabase.Create();
        var prompts = ArkCompositionNativePromptTests.Service(database);
        var core = new ObservedLightningHandler(ArkCompositionNativePromptTests.Client(prompts));
        using var services = AddNativeLightningDependencies(new ServiceCollection())
            .AddSingleton<IPaymentMethodHandler>(core)
            .AddSingleton(prompts)
            .AddSingleton<PaymentMethodHandlerDictionary>()
            .AddArkCompositionOnchainPrompts()
            .BuildServiceProvider();
        var handler = Assert.IsAssignableFrom<LightningLikePaymentHandler>(
            services.GetRequiredService<PaymentMethodHandlerDictionary>()[Lightning]);

        // Both LNURL prompt creation and the store settings controller concrete-cast this dictionary entry
        // and call GetNodeInfo, so the decorator must retain the complete native handler state.
        var error = await Record.ExceptionAsync(() => handler.GetNodeInfo(
            new LightningPaymentMethodConfig(), null, throws: true));

        Assert.IsType<PaymentMethodUnavailableException>(error);
    }

    [Fact]
    public async Task InvoicePromptLifecycleBindsEachLightningRenewalBeforeVerifiedDeliverySettlesIt()
    {
        await using var database = await CompositionDatabase.Create();
        var prompts = ArkCompositionNativePromptTests.Service(database);
        var core = new ObservedLightningHandler(ArkCompositionNativePromptTests.Client(prompts));
        using var services = AddNativeLightningDependencies(new ServiceCollection())
            .AddSingleton<IPaymentMethodHandler>(core).AddSingleton(prompts)
            .AddSingleton<PaymentMethodHandlerDictionary>().AddArkCompositionOnchainPrompts().BuildServiceProvider();
        var handlers = services.GetRequiredService<PaymentMethodHandlerDictionary>();
        Assert.IsType<ArkCompositionLightningPaymentHandler>(handlers[Lightning]);
        var store = ArkCompositionPromptTests.Store();
        store.SetPaymentMethodConfig(core, new LightningPaymentMethodConfig());
        var invoice = ArkCompositionNativePromptTests.Invoice();

        for (var activation = 0; activation < 2; activation++)
        {
            var context = new PaymentMethodContext(store, new StoreBlob(), store.GetPaymentMethodConfig(Lightning)!,
                handlers[Lightning], invoice, new InvoiceLogs());
            await context.BeforeFetchingRates();
            await context.CreatePaymentPrompt();
            invoice.SetPaymentPrompt(Lightning, context.Prompt);
            await context.ActivatingPaymentPrompt();
            var details = Assert.IsType<JObject>(context.Prompt.Details);
            Assert.Contains(details.Properties(), property => string.Equals(property.Name, "InvoiceId", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(details.Properties(), property => string.Equals(property.Name, "PaymentHash", StringComparison.OrdinalIgnoreCase));
        }

        var unfiltered = await database.Repository.List("store", paymentMethodId: "BTC-LN");
        Assert.Equal(2, unfiltered.Count);
        var routes = await database.Repository.List("store", "invoice", "BTC-LN");
        Assert.Equal(2, routes.Count);
        Assert.Equal(2, routes.Select(route => route.PaymentHash).Distinct().Count());
        Assert.All(routes, route => Assert.Equal("invoice", route.InvoiceId));

        var sink = new SettlingSink(invoice, handlers[Lightning]);
        var execution = new ArkCompositionExecutionService(new ArkCompositionExecutionJournal(database, database.Repository),
            new VerifiedDeliveryBackend(), PromptExecutionLock.Available, sink);
        foreach (var route in routes)
            await execution.AdvanceAsync("store", route.RouteId);

        Assert.Equal(2, sink.Payments.Count);
        Assert.All(sink.Payments, payment => Assert.Equal(PaymentStatus.Settled, payment.Status));
        Assert.Equal(routes.Select(route => route.PaymentHash).OrderBy(paymentHash => paymentHash),
            sink.Payments.Select(payment => payment.Id).OrderBy(paymentId => paymentId));
    }

    private static ServiceCollection AddNativeLightningDependencies(ServiceCollection services)
    {
        var http = TestProxy.Create<IHttpClientFactory>((_, _) => new HttpClient());
        var policies = TestProxy.Create<ISettingsAccessor<PoliciesSettings>>((method, _) =>
            method.Name == "get_Settings" ? new PoliciesSettings() : throw new NotSupportedException(method.Name));
        services.AddSingleton(new NBXplorerDashboard());
        services.AddSingleton(new LightningClientFactoryService(http, [], []));
        services.AddSingleton(new SocketFactory(new BTCPayServerOptions()));
        services.AddSingleton(policies);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new LightningNetworkOptions()));
        return services;
    }

    private sealed class ObservedLightningHandler(ArkLightningClient client) : LightningLikePaymentHandler(Lightning,
        null!, null!, ArkCompositionOnchainPromptTests.Network(), null!, Microsoft.Extensions.Options.Options.Create(new LightningNetworkOptions()), null!,
        Microsoft.Extensions.Options.Options.Create(new LightningNetworkOptions())), IPaymentMethodHandler
    {
        Task IPaymentMethodHandler.BeforeFetchingRates(PaymentMethodContext context)
        {
            context.Prompt.Currency = "BTC";
            context.Prompt.Divisibility = 11;
            return Task.CompletedTask;
        }

        async Task IPaymentMethodHandler.ConfigurePrompt(PaymentMethodContext context)
        {
            var created = await client.CreateInvoice(LightMoney.Satoshis(1000), "order", TimeSpan.FromMinutes(5));
            context.Prompt.Destination = created.BOLT11;
            context.Prompt.Details = JObject.FromObject(new LigthningPaymentPromptDetails
            {
                InvoiceId = created.Id,
                PaymentHash = uint256.Parse(created.PaymentHash!),
                Preimage = null!
            }, Serializer);
        }
    }

    private sealed class VerifiedDeliveryBackend : IArkCompositionExecutionBackend
    {
        public Task<ArkCompositionObservation> ObserveAsync(ArkInvoiceComposition route, CancellationToken cancellationToken) =>
            Task.FromResult(new ArkCompositionObservation(new(CompositionFixture.MFundingTx, 1000),
                CompositionFixture.ClaimTx, new(CompositionFixture.ClaimTx, 1000)));

        public Task<ArkCompositionExecutionOutcome> AdvanceAsync(ArkInvoiceComposition route, CancellationToken cancellationToken) =>
            Task.FromResult(new ArkCompositionExecutionOutcome(CompositionFixture.LockProof(), CompositionFixture.EvmClaimTx, "2000000"));
    }

    private sealed class SettlingSink(InvoiceEntity invoice, IPaymentMethodHandler handler) : IArkCompositionPaymentSink
    {
        public List<PaymentData> Payments { get; } = [];

        public Task SettleAsync(ArkInvoiceComposition route, CancellationToken cancellationToken)
        {
            Payments.AddRange(ArkCompositionPaymentSink.CreatePayments(route, invoice, handler));
            return Task.CompletedTask;
        }
    }
}
