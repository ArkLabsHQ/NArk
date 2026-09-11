using System.Security.Cryptography;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionPromptTests
{
    [Fact]
    public async Task CreatedPromptWarmsExactCheckoutBindingWithoutARepositoryReadDuringRendering()
    {
        await using var database = await CompositionDatabase.Create();
        var invoice = ArkCompositionNativePromptTests.Invoice();
        var store = Store();
        var policy = new ArkCompositionCheckoutPolicy(
            TestProxy.Create<IArkCompositionContextSource>((_, _) => throw new InvalidOperationException("unexpected preload")),
            database.Repository, new MemoryCache(new MemoryCacheOptions()));
        var service = new ArkCompositionPromptService(database.Repository, new PromptExecutor(database.Repository),
            executionLock: PromptExecutionLock.Available, checkoutPolicy: policy);

        var route = (await service.TryCreateAsync(store, invoice, "BTC-CHAIN", 1000, Expiry))!;
        var prompt = new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, Destination = route.CustomerDestination!,
            Details = new JObject
            {
                ["compositionRouteId"] = route.RouteId.ToString("N"), ["paymentHash"] = route.PaymentHash
            }
        };
        invoice.SetPaymentPrompt(PaymentMethodId.Parse("BTC-CHAIN"), prompt);

        Assert.True(policy.RequiresComposition(invoice, store));
        Assert.True(policy.CanInclude(prompt, true));
    }

    [Theory]
    [InlineData("ARKADE", 1800000030)]
    [InlineData("BTC-LN", 1800000020)]
    [InlineData("BTC-CHAIN", 1800000020)]
    public async Task CustomerDestinationAndBoundedExpirySurviveReload(string rail, long expiry)
    {
        await using var database = await CompositionDatabase.Create();
        var service = new ArkCompositionPromptService(database.Repository, new PromptExecutor(database.Repository), executionLock: PromptExecutionLock.Available);
        var route = (await service.TryCreateAsync(Store(), null, rail, 1000, Expiry))!;
        var stored = JObject.FromObject((await database.Repository.Get("store", route.RouteId))!);

        Assert.Equal(rail == "ARKADE" ? CompositionFixture.OutgoingQuote().LockupAddress
            : rail == "BTC-LN" ? "lnbcrt1testpublicquote" : "bcrt1ppublichtlc", stored.Value<string>("CustomerDestination"));
        Assert.Equal(expiry, stored.Value<long>("CheckoutExpiresAt"));
        Assert.False(stored.Value<bool>("SettlementVerified"));
    }

    [Theory]
    [InlineData("ARKADE")]
    [InlineData("BTC-LN")]
    [InlineData("BTC-CHAIN")]
    public async Task SdkCheckoutSafetyRejectsPromptsWithLessThanTheMinimumWindow(string rail)
    {
        await using var database = await CompositionDatabase.Create();
        var service = new ArkCompositionPromptService(database.Repository, new PromptExecutor(database.Repository),
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1800000000)), executionLock: PromptExecutionLock.Available);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TryCreateAsync(Store(), null, rail, 1000, Expiry));

        Assert.Equal("The composed quote leaves too little checkout time.", error.Message);
        Assert.Null(Assert.Single(await database.Repository.List("store")).CustomerDestination);
    }

    [Theory]
    [InlineData("ARKADE", 1800000025)]
    [InlineData("BTC-LN", 1800000015)]
    [InlineData("BTC-CHAIN", 1800000015)]
    public async Task PromptExpiryUsesTheConfiguredSdkFundingSafety(string rail, long expectedExpiry)
    {
        await using var database = await CompositionDatabase.Create();
        var service = new ArkCompositionPromptService(database.Repository, new PromptExecutor(database.Repository),
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1800000000)),
            executionLock: PromptExecutionLock.Available,
            timingOptions: new() { OutgoingFundingSafetySeconds = 35, MinimumCheckoutWindowSeconds = 1 });

        var route = (await service.TryCreateAsync(Store(), null, rail, 1000,
            DateTimeOffset.FromUnixTimeSeconds(1800000100)))!;

        Assert.Equal(expectedExpiry, route.CheckoutExpiresAt);
    }

    [Fact]
    public async Task EachRailAndRenewalPersistsAnIndependentSdkHashBeforeQuoting()
    {
        await using var database = await CompositionDatabase.Create();
        var executor = new PromptExecutor(database.Repository);
        var service = new ArkCompositionPromptService(database.Repository, executor, executionLock: PromptExecutionLock.Available);
        var invoice = new InvoiceEntity { Id = "invoice", StoreId = "store" };
        foreach (var rail in new[] { "ARKADE", "BTC-LN", "BTC-CHAIN", "ARKADE", "BTC-LN", "BTC-CHAIN" })
            Assert.NotNull(await service.TryCreateAsync(Store(), invoice, rail, 1000, Expiry));

        var rows = await database.Repository.List("store", "invoice");
        Assert.Equal(6, rows.Count);
        Assert.Equal(6, executor.Secrets.Count);
        Assert.Equal(6, rows.Select(r => r.PaymentHash).Distinct().Count());
        Assert.Equal(10, rows.SelectMany(r => r.Legs).Select(l => l.RfqId).Distinct().Count());
        Assert.All(rows, route => Assert.False(route.SettlementVerified));
        var serialized = System.Text.Json.JsonSerializer.Serialize(rows);
        Assert.DoesNotContain("Preimage", serialized);
        Assert.All(executor.Secrets, secret => Assert.DoesNotContain(Convert.ToHexString(secret), serialized, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ARKADE")]
    [InlineData("BTC-LN")]
    [InlineData("BTC-CHAIN")]
    public async Task DisabledRailDoesNotCreateOrNegotiateARoute(string rail)
    {
        await using var database = await CompositionDatabase.Create();
        var executor = new PromptExecutor(database.Repository);
        var service = new ArkCompositionPromptService(database.Repository, executor, executionLock: PromptExecutionLock.Available);
        var store = Store(CompositionFixture.Settings() with
        {
            RoutePolicy = CompositionFixture.Settings().RoutePolicy! with { EnabledSourceRails = [rail == "ARKADE" ? "BTC-LN" : "ARKADE"] }
        });

        Assert.Null(await service.TryCreateAsync(store, null, rail, 1000, Expiry));
        Assert.Empty(await database.Repository.List("store"));
        Assert.Empty(executor.Secrets);
    }

    [Theory]
    [InlineData("absent", false)]
    [InlineData("excluded", false)]
    [InlineData("enabled", true)]
    public async Task ArkadePromptRequiresAnEnabledPaymentMethod(string configuration, bool expectedPrompt)
    {
        await using var database = await CompositionDatabase.Create();
        var executor = new PromptExecutor(database.Repository);
        var service = new ArkCompositionPromptService(database.Repository, executor,
            executionLock: PromptExecutionLock.Available);
        var store = configuration == "absent" ? new StoreData { Id = "store" } : Store();
        if (configuration == "excluded")
            store.StoreBlob = "{\"excludedPaymentMethods\":[\"ARKADE\"]}";

        var route = await service.TryCreateAsync(store, null, "ARKADE", 1000, Expiry);

        Assert.Equal(expectedPrompt, route is not null);
        Assert.Equal(expectedPrompt ? 1 : 0, (await database.Repository.List("store")).Count);
        Assert.Equal(expectedPrompt ? 1 : 0, executor.Secrets.Count);
    }

    [Fact]
    public async Task WrongWalletCannotUseAnotherStoresCompositionPolicy()
    {
        await using var database = await CompositionDatabase.Create();
        var executor = new PromptExecutor(database.Repository);
        var service = new ArkCompositionPromptService(database.Repository, executor, executionLock: PromptExecutionLock.Available);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryCreateAsync(Store(), null, "BTC-LN", 1000, Expiry, "other-wallet"));
        Assert.Empty(executor.Secrets);
        Assert.Empty(await database.Repository.List("store"));
    }

    [Fact]
    public async Task MissingAdapterNeverCreatesAPayablePrompt()
    {
        await using var database = await CompositionDatabase.Create();
        var service = new ArkCompositionPromptService(database.Repository);

        var error = await Assert.ThrowsAsync<ArkCompositionUnavailableException>(() => service.TryCreateAsync(Store(), null, "ARKADE", 1000, Expiry));
        Assert.Equal("sdk-composition-unavailable", error.Code);
        Assert.Empty(await database.Repository.List("store"));
    }

    [Fact]
    public async Task MissingCrossProcessLockNeverCreatesAPayablePrompt()
    {
        await using var database = await CompositionDatabase.Create();
        var executor = new PromptExecutor(database.Repository);
        var service = new ArkCompositionPromptService(database.Repository, executor, executionLock: new PromptExecutionLock(false));

        var error = await Assert.ThrowsAsync<ArkCompositionUnavailableException>(() => service.TryCreateAsync(Store(), null, "BTC-LN", 1000, Expiry));

        Assert.Equal("cross-process-execution-lock-unavailable", error.Code);
        Assert.Empty(executor.Secrets);
        Assert.Empty(await database.Repository.List("store"));
    }

    [Fact]
    public async Task FailedOutgoingQuoteRetainsReservedIdsWithoutLeakingAdapterErrors()
    {
        await using var database = await CompositionDatabase.Create();
        var executor = new PromptExecutor(database.Repository) { FailOutgoing = true };
        var service = new ArkCompositionPromptService(database.Repository, executor, executionLock: PromptExecutionLock.Available);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryCreateAsync(Store(), null, "BTC-LN", 1000, Expiry));

        Assert.DoesNotContain("sensitive-adapter-payload", error.ToString());
        var stored = Assert.Single(await database.Repository.List("store"));
        Assert.Equal("Prepared", stored.Status);
        Assert.Equal(2, stored.Legs.Count);
        Assert.Null(stored.CustomerDestination);
        Assert.Null(stored.CheckoutExpiresAt);
    }

    internal static DateTimeOffset Expiry => DateTimeOffset.FromUnixTimeSeconds(1800000040);
    internal static StoreData Store(ArkEvmSettlementSettings? settings = null)
    {
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, null!);
        var store = new StoreData { Id = "store" };
        store.SetPaymentMethodConfig(handler, new ArkadePaymentMethodConfig("private-wallet")
        {
            EvmSettlement = settings ?? CompositionFixture.Settings()
        });
        return store;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class PromptExecutor(ArkInvoiceCompositionRepository repository) : IArkCompositionExecutor
{
    internal List<byte[]> Secrets { get; } = [];
    internal bool FailOutgoing { get; init; }

    public async Task<ArkCompositionPreparation> PrepareAsync(ArkCompositionExecutionRequest request, CancellationToken cancellationToken)
    {
        Assert.Equal("PendingSdk", (await repository.Get("store", request.RouteId, cancellationToken))!.Status);
        var secret = RandomNumberGenerator.GetBytes(32);
        Secrets.Add(secret);
        return new(Convert.ToHexString(SHA256.HashData(secret)).ToLowerInvariant(), Id(), request.SourceRail == "ARKADE" ? null : Id());
    }

    public async Task<ArkCompositionOutgoingQuote> QuoteOutgoingAsync(ArkCompositionExecutionRequest request,
        ArkCompositionPreparation prepared, CancellationToken cancellationToken)
    {
        var stored = (await repository.Get("store", request.RouteId, cancellationToken))!;
        Assert.Equal("Prepared", stored.Status);
        Assert.Equal(prepared.PaymentHash, stored.PaymentHash);
        Assert.Contains(stored.Legs, l => l.RfqId == prepared.OutgoingRfqId);
        if (FailOutgoing) throw new InvalidOperationException("sensitive-adapter-payload");
        return new(CompositionFixture.OutgoingQuote() with { RfqId = prepared.OutgoingRfqId, PaymentHash = prepared.PaymentHash },
            CompositionFixture.EvmTerms() with { PaymentHash = prepared.PaymentHash });
    }

    public async Task<ArkCompositionIngressQuote> QuoteIngressAsync(ArkCompositionExecutionRequest request,
        ArkCompositionPreparation prepared, ArkCompositionQuote outgoing, CancellationToken cancellationToken)
    {
        var stored = (await repository.Get("store", request.RouteId, cancellationToken))!;
        Assert.Equal("OutgoingQuoted", stored.Status);
        Assert.Contains(stored.Legs, l => l.RfqId == prepared.IngressRfqId);
        Assert.Equal(stored.PaymentHash, outgoing.PaymentHash);
        return new(CompositionFixture.IngressQuote() with { RfqId = prepared.IngressRfqId!, PaymentHash = prepared.PaymentHash },
            request.SourceRail == "BTC-LN" ? "lnbcrt1testpublicquote" : "bcrt1ppublichtlc", 1800000030);
    }

    private static string Id() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

internal sealed class PromptExecutionLock(bool supportsCrossProcessExecution) : IArkCompositionExecutionLock
{
    internal static PromptExecutionLock Available { get; } = new(true);
    public bool SupportsCrossProcessExecution => supportsCrossProcessExecution;
    public ValueTask<IAsyncDisposable> AcquireAsync(string scope, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IAsyncDisposable>(new Lease());

    private sealed class Lease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
