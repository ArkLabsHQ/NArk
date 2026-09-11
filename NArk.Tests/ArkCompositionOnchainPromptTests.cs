using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionOnchainPromptTests
{
    internal static readonly PaymentMethodId Onchain = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
    internal static BTCPayNetwork Network() => new()
    {
        CryptoCode = "BTC", Divisibility = 8,
        NBXplorerNetwork = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC")
    };

    [Fact]
    public async Task InvoiceCreationPipelinePublishesSolverDestinationWithoutCoreTrackingOrSettlement()
    {
        await using var database = await CompositionDatabase.Create();
        var core = new ObservedBitcoinHandler();
        using var services = Services(core, ArkCompositionNativePromptTests.Service(database));
        var handlers = services.GetRequiredService<PaymentMethodHandlerDictionary>();
        var store = Store(core);
        var invoice = ArkCompositionNativePromptTests.Invoice();
        var creation = new InvoiceCreationContext(store, new StoreBlob(), invoice, new InvoiceLogs(), handlers, null);

        await creation.BeforeFetchingRates();
        await creation.CreatePaymentPrompts();
        var context = creation.PaymentMethodContexts[Onchain];
        invoice.SetPaymentPrompt(Onchain, context.Prompt);
        await creation.ActivatingPaymentPrompt();

        var route = Assert.Single(await database.Repository.List("store", "invoice", "BTC-CHAIN"));
        Assert.Equal(PaymentMethodContext.ContextStatus.Created, context.Status);
        Assert.Equal(route.CustomerDestination, invoice.GetPaymentPrompt(Onchain)!.Destination);
        Assert.Equal(1025, Money.Coins(context.Prompt.Calculate().Due).Satoshi);
        Assert.Equal(25, Money.Coins(context.Prompt.PaymentMethodFee).Satoshi);
        Assert.Empty(context.TrackedDestinations);
        Assert.Null(handlers.GetDerivationStrategy(invoice, core.Network));
        Assert.False(((BitcoinPaymentPromptDetails)handlers.ParsePaymentPromptDetails(context.Prompt)!).PayjoinEnabled);
        Assert.False(route.SettlementVerified);
        Assert.Empty(invoice.GetPayments(false));
        Assert.Equal(BTCPayServer.Client.Models.InvoiceStatus.New, invoice.Status);
        Assert.Empty(core.Calls);
        Assert.Single(handlers, h => h.PaymentMethodId == Onchain);
        Assert.IsAssignableFrom<BitcoinLikePaymentHandler>(handlers[Onchain]);
    }

    [Fact]
    public async Task LazyCreationAndEachActivationOrRenewalUseIndependentRoutes()
    {
        await using var database = await CompositionDatabase.Create();
        var core = new ObservedBitcoinHandler();
        using var services = Services(core, ArkCompositionNativePromptTests.Service(database));
        var handlers = services.GetRequiredService<PaymentMethodHandlerDictionary>();
        var store = Store(core);
        var invoice = ArkCompositionNativePromptTests.Invoice();
        var creation = new InvoiceCreationContext(store, new StoreBlob(), invoice, new InvoiceLogs(), handlers, null);
        creation.SetLazyActivation(true);
        await creation.BeforeFetchingRates();
        await creation.CreatePaymentPrompts();
        await creation.ActivatingPaymentPrompt();
        Assert.Empty(await database.Repository.List("store"));
        Assert.Empty(core.Calls);

        for (var activation = 0; activation < 2; activation++)
        {
            var context = new PaymentMethodContext(store, new StoreBlob(), store.GetPaymentMethodConfig(Onchain)!,
                handlers[Onchain], invoice, new InvoiceLogs());
            await context.BeforeFetchingRates();
            await context.CreatePaymentPrompt();
            invoice.SetPaymentPrompt(Onchain, context.Prompt);
            await context.ActivatingPaymentPrompt();
            Assert.Equal(PaymentMethodContext.ContextStatus.Created, context.Status);
        }

        var routes = await database.Repository.List("store", "invoice", "BTC-CHAIN");
        Assert.Equal(2, routes.Count);
        Assert.Equal(2, routes.Select(r => r.PaymentHash).Distinct().Count());
        Assert.Empty(core.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterFailureNeverFallsBackToCoreWalletAddress(bool quoteFailure)
    {
        await using var database = await CompositionDatabase.Create();
        var core = new ObservedBitcoinHandler();
        var prompts = new ArkCompositionPromptService(database.Repository,
            quoteFailure ? new PromptExecutor(database.Repository) { FailOutgoing = true } : null);
        using var services = Services(core, prompts);
        var store = Store(core);
        var creation = new InvoiceCreationContext(store, new StoreBlob(), ArkCompositionNativePromptTests.Invoice(),
            new InvoiceLogs(), services.GetRequiredService<PaymentMethodHandlerDictionary>(), null);
        await creation.BeforeFetchingRates();
        await creation.CreatePaymentPrompts();
        await creation.ActivatingPaymentPrompt();

        var context = creation.PaymentMethodContexts[Onchain];
        Assert.Equal(PaymentMethodContext.ContextStatus.Failed, context.Status);
        Assert.Null(context.Prompt.Destination);
        Assert.Empty(context.TrackedDestinations);
        Assert.Empty(core.Calls);
        Assert.All(await database.Repository.List("store"), route => Assert.Null(route.CustomerDestination));
    }

    [Fact]
    public async Task OrdinaryStoreDelegatesEveryPromptPhaseAndKeepsItsOriginalDetails()
    {
        await using var database = await CompositionDatabase.Create();
        var core = new ObservedBitcoinHandler();
        using var services = Services(core, ArkCompositionNativePromptTests.Service(database));
        var handlers = services.GetRequiredService<PaymentMethodHandlerDictionary>();
        var store = Store(core, false);
        var creation = new InvoiceCreationContext(store, new StoreBlob(), ArkCompositionNativePromptTests.Invoice(),
            new InvoiceLogs(), handlers, null);
        await creation.BeforeFetchingRates();
        await creation.CreatePaymentPrompts();
        await creation.ActivatingPaymentPrompt();

        Assert.Equal(new[] { "rates", "prompt", "saved" }, core.Calls);
        var context = creation.PaymentMethodContexts[Onchain];
        Assert.Equal("merchant-wallet-address", context.Prompt.Destination);
        Assert.Equal(new[] { "merchant-wallet-script" }, context.TrackedDestinations);
        Assert.Equal("original", context.Prompt.Details.Value<string>("marker"));
        Assert.Empty(await database.Repository.List("store"));
    }

    [Fact]
    public async Task CoreConfigurationAndPaymentParsingRemainUnchanged()
    {
        await using var database = await CompositionDatabase.Create();
        var network = Network();
        var core = new BitcoinLikePaymentHandler(Onchain, null!, network, null!, null!, null!, null!, null!);
        IPaymentMethodHandler composed = new ArkCompositionOnchainPaymentHandler(core,
            ArkCompositionNativePromptTests.Service(database), null!, null!, null!, null!, null!, null!);
        var config = JToken.FromObject(new DerivationSchemeSettings(), core.Serializer);
        Assert.True(JToken.DeepEquals(JToken.FromObject(core.ParsePaymentMethodConfig(config), core.Serializer),
            JToken.FromObject(composed.ParsePaymentMethodConfig(config), core.Serializer)));
        var details = JToken.FromObject(new BitcoinPaymentPromptDetails { PayjoinEnabled = true,
            RecommendedFeeRate = new FeeRate(3m) }, core.Serializer);
        Assert.True(JToken.DeepEquals(JToken.FromObject(core.ParsePaymentPromptDetails(details), core.Serializer),
            JToken.FromObject(composed.ParsePaymentPromptDetails(details), core.Serializer)));
        var payment = JToken.FromObject(new BitcoinLikePaymentData(new OutPoint(uint256.One, 0), true, new KeyPath("0/1"), 1), core.Serializer);
        Assert.True(JToken.DeepEquals(JToken.FromObject(core.ParsePaymentDetails(payment), core.Serializer),
            JToken.FromObject(composed.ParsePaymentDetails(payment), core.Serializer)));
    }

    internal static StoreData Store(IPaymentMethodHandler handler, bool composed = true)
    {
        var store = ArkCompositionPromptTests.Store(CompositionFixture.Settings() with { Enabled = composed });
        store.SetPaymentMethodConfig(handler, new DerivationSchemeSettings());
        return store;
    }

    private static ServiceProvider Services(IPaymentMethodHandler core, ArkCompositionPromptService prompts)
    {
        var services = new ServiceCollection()
            .AddSingleton(core)
            .AddSingleton(prompts)
            .AddSingleton((ExplorerClientProvider)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(ExplorerClientProvider)))
            .AddSingleton(TestProxy.Create<IFeeProviderFactory>())
            .AddSingleton((DisplayFormatter)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(DisplayFormatter)))
            .AddSingleton(new NBXplorerDashboard())
            .AddSingleton((WalletRepository)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(WalletRepository)))
            .AddSingleton((BTCPayWalletProvider)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(BTCPayWalletProvider)))
            .AddSingleton<PaymentMethodHandlerDictionary>()
            .AddArkCompositionOnchainPrompts();
        return services.BuildServiceProvider();
    }

    private sealed class ObservedBitcoinHandler() : BitcoinLikePaymentHandler(Onchain, null!, Network(), null!, null!, null!, null!, null!), IPaymentMethodHandler
    {
        internal List<string> Calls { get; } = [];
        Task IPaymentMethodHandler.BeforeFetchingRates(PaymentMethodContext context)
        {
            Calls.Add("rates");
            context.Prompt.Currency = "BTC";
            context.Prompt.Divisibility = 8;
            return Task.CompletedTask;
        }
        Task IPaymentMethodHandler.ConfigurePrompt(PaymentMethodContext context)
        {
            Calls.Add("prompt");
            context.Prompt.Destination = "merchant-wallet-address";
            context.Prompt.Details = new JObject { ["marker"] = "original" };
            context.TrackedDestinations.Add("merchant-wallet-script");
            return Task.CompletedTask;
        }
        Task IPaymentMethodHandler.AfterSavingInvoice(PaymentMethodContext context)
        {
            Calls.Add("saved");
            return Task.CompletedTask;
        }
    }
}
