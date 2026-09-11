using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionCheckoutTests
{
    [Fact]
    public async Task SynchronousCheckoutPolicyReadsOnlyPreloadedState()
    {
        await using var database = await CompositionDatabase.Create();
        var sourceCalls = 0;
        var source = TestProxy.Create<IArkCompositionContextSource>((_, _) =>
        {
            sourceCalls++;
            return Task.FromResult<StoreData?>(ArkCompositionPromptTests.Store());
        });
        var policy = new ArkCompositionCheckoutPolicy(source, database.Repository,
            new MemoryCache(new MemoryCacheOptions()));

        Assert.False(policy.RequiresComposition(ArkCompositionNativePromptTests.Invoice()));
        Assert.Equal(0, sourceCalls);
    }

    [Fact]
    public async Task AsyncPreloadCachesStoreAndExactRouteForSynchronousCheckoutRendering()
    {
        var database = await CompositionDatabase.Create();
        var invoice = ArkCompositionNativePromptTests.Invoice();
        var prompt = await OnchainPrompt(database, invoice);
        var policy = Policy(database);

        await policy.PreloadAsync(invoice);
        await database.DisposeAsync();

        Assert.True(policy.RequiresComposition(invoice));
        Assert.True(policy.CanInclude(prompt, true));
        prompt.Destination = "merchant-wallet-address";
        Assert.False(policy.CanInclude(prompt, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledSettlementNeverEmbedsAnOrdinaryWalletOrBoardingDestination(bool arkRouteMarker)
    {
        await using var database = await CompositionDatabase.Create();
        var invoice = ArkCompositionNativePromptTests.Invoice();
        var ark = ArkPrompt(invoice, arkRouteMarker);
        invoice.SetPaymentPrompt(ArkCompositionOnchainPromptTests.Onchain, new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, Destination = "merchant-wallet-address", Details = new JObject()
        });
        invoice.SetPaymentPrompt(PaymentTypes.LN.GetPaymentMethodId("BTC"), new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, Destination = "ordinary-lightning", Details = new JObject()
        });
        var policy = Policy(database);
        var link = new ArkadePaymentLinkExtension(new ServiceCollection().BuildServiceProvider(), null!, policy);

        var uri = link.GetPaymentLink(ark, null);

        Assert.StartsWith("bitcoin:?", uri);
        Assert.DoesNotContain("merchant-wallet", uri);
        Assert.DoesNotContain("boarding", uri);
        Assert.DoesNotContain("ordinary-lightning", uri);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("address")]
    [InlineData("hash")]
    [InlineData("inactive")]
    [InlineData("fee")]
    public async Task UnifiedLinkOmitsUnboundInactiveOrAmountIncompatibleOnchainPrompt(string invalid)
    {
        await using var database = await CompositionDatabase.Create();
        var invoice = ArkCompositionNativePromptTests.Invoice();
        var ark = ArkPrompt(invoice);
        var onchain = await OnchainPrompt(database, invoice);
        if (invalid == "unknown") onchain.Details["compositionRouteId"] = Guid.NewGuid().ToString();
        if (invalid == "address") onchain.Destination = "merchant-wallet-address";
        if (invalid == "hash") onchain.Details["paymentHash"] = new string('f', 64);
        if (invalid == "inactive") onchain.Inactive = true;
        if (invalid == "fee") onchain.PaymentMethodFee = 0.00000025m;
        invoice.SetPaymentPrompt(ArkCompositionOnchainPromptTests.Onchain, onchain);
        var link = new ArkadePaymentLinkExtension(new ServiceCollection().BuildServiceProvider(), null!, Policy(database));

        Assert.StartsWith("bitcoin:?", link.GetPaymentLink(ark, null));
    }

    [Fact]
    public async Task UnifiedLinkAndQrPreservePluginValuesAndRejectPaymentOverrides()
    {
        await using var database = await CompositionDatabase.Create();
        var invoice = ArkCompositionNativePromptTests.Invoice();
        var ark = ArkPrompt(invoice);
        var onchain = await OnchainPrompt(database, invoice);
        var policy = Policy(database);
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, null!);
        var upstream = new UpstreamLink();
        using var services = new ServiceCollection().AddSingleton(policy)
            .AddSingleton<IPaymentLinkExtension>(upstream)
            .AddSingleton<IGlobalCheckoutModelExtension>(new UpstreamGlobal()).BuildServiceProvider();
        var link = new ArkadePaymentLinkExtension(services, null!, policy);
        var handlers = new PaymentMethodHandlerDictionary([handler,
            new BTCPayServer.Payments.Bitcoin.BitcoinLikePaymentHandler(ArkCompositionOnchainPromptTests.Onchain,
                null!, ArkCompositionOnchainPromptTests.Network(), null!, null!, null!, null!, null!)]);
        ICheckoutModelExtension checkout = new ArkadeCheckoutModelExtension([link, upstream], services, handlers,
            handler, NullLogger<ArkadeCheckoutModelExtension>.Instance);
        var model = new CheckoutModel { PaymentMethodCurrency = "BTC" };

        checkout.ModifyCheckoutModel(new CheckoutModelContext(model, ArkCompositionPromptTests.Store(), new StoreBlob(),
            invoice, null!, ark, handler));

        Assert.StartsWith("bitcoin:" + onchain.Destination + "?", model.InvoiceBitcoinUrl);
        Assert.StartsWith("bitcoin:" + onchain.Destination.ToUpperInvariant() + "?", model.InvoiceBitcoinUrlQR);
        foreach (var uri in new[] { model.InvoiceBitcoinUrl, model.InvoiceBitcoinUrlQR })
        {
            Assert.Contains("branta_id=AbC%2F%2B", uri);
            Assert.Contains("branta_secret=Secret%3D%2F", uri);
            Assert.DoesNotContain("evil", uri);
            Assert.DoesNotContain("pj=", uri);
            Assert.DoesNotContain("pjos=", uri);
            Assert.Equal(1, uri.Split("amount=").Length - 1);
            Assert.Equal(1, uri.Split("ark=").Length - 1);
        }
    }

    [Fact]
    public async Task OrdinaryUnifiedLinkRetainsOriginalOnchainPayjoinAndPluginParameters()
    {
        await using var database = await CompositionDatabase.Create();
        var invoice = ArkCompositionNativePromptTests.Invoice();
        var ark = ArkPrompt(invoice, false);
        invoice.SetPaymentPrompt(ArkCompositionOnchainPromptTests.Onchain, new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, Destination = "merchant-wallet-address", Details = new JObject()
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("arkade-lightning-store", false);
        using var availability = new ArkadeLightningAvailabilityService(null!, new EventAggregator(new BTCPayServer.Logging.Logs()), cache, null!, null!);
        using var services = new ServiceCollection().AddSingleton<IPaymentLinkExtension>(new UpstreamLink()).BuildServiceProvider();
        var link = new ArkadePaymentLinkExtension(services, availability,
            Policy(database, ArkCompositionPromptTests.Store(CompositionFixture.Settings() with { Enabled = false })));

        var uri = link.GetPaymentLink(ark, null);

        Assert.StartsWith("bitcoin:merchant-wallet-address?", uri);
        Assert.Contains("pj=original-endpoint", uri);
        Assert.Contains("branta_id=AbC%2F%2B", uri);
    }

    private static ArkCompositionCheckoutPolicy Policy(CompositionDatabase database, StoreData? store = null)
    {
        var owner = store ?? ArkCompositionPromptTests.Store();
        var policy = new ArkCompositionCheckoutPolicy(
            TestProxy.Create<IArkCompositionContextSource>((_, _) => Task.FromResult<StoreData?>(owner)),
            database.Repository, new MemoryCache(new MemoryCacheOptions()));
        policy.RememberStore(owner);
        return policy;
    }

    private static PaymentPrompt ArkPrompt(InvoiceEntity invoice, bool composed = true)
    {
        var prompt = new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, Destination = CompositionFixture.OutgoingQuote().LockupAddress,
            Details = new JObject { ["boardingAddress"] = "ordinary-boarding-address" }
        };
        if (composed) prompt.Details["compositionRouteId"] = Guid.NewGuid().ToString();
        invoice.SetPaymentPrompt(PaymentMethodId.Parse("ARKADE"), prompt);
        return prompt;
    }

    private static async Task<PaymentPrompt> OnchainPrompt(CompositionDatabase database, InvoiceEntity invoice)
    {
        var route = CompositionFixture.Prepared("BTC-CHAIN");
        route.AttachInvoice(invoice, ArkCompositionOnchainPromptTests.Onchain, route.PaymentHash!);
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        route.RecordIngressQuote(CompositionFixture.IngressQuote() with { FromAmount = "1000" });
        route.RecordCustomerPrompt(CompositionFixture.IngressQuote().LockupAddress, 1800000030);
        await database.Repository.Add("store", route);
        var prompt = new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, Destination = route.CustomerDestination!,
            Details = new JObject { ["compositionRouteId"] = route.RouteId.ToString(), ["paymentHash"] = route.PaymentHash }
        };
        invoice.SetPaymentPrompt(ArkCompositionOnchainPromptTests.Onchain, prompt);
        return prompt;
    }

    private sealed class UpstreamLink : IPaymentLinkExtension
    {
        public PaymentMethodId PaymentMethodId => ArkCompositionOnchainPromptTests.Onchain;
        public string GetPaymentLink(PaymentPrompt prompt, IUrlHelper? helper) => "bitcoin:" + prompt.Destination +
            "?amount=0.00001&branta_id=AbC%2F%2B&pj=original-endpoint&pjos=0&%61rk=evil";
    }

    private sealed class UpstreamGlobal : IGlobalCheckoutModelExtension
    {
        public void ModifyCheckoutModel(CheckoutModelContext context)
        {
            const string suffix = "&branta_secret=Secret%3D%2F&l%69ghtning=evil&pj=evil";
            context.Model.InvoiceBitcoinUrl += suffix;
            context.Model.InvoiceBitcoinUrlQR += suffix;
        }
    }
}
