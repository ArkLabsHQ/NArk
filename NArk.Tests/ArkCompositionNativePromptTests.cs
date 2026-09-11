using BTCPayServer;
using BTCPayServer.Logging;
using InvoiceStatus = BTCPayServer.Client.Models.InvoiceStatus;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using NArk.Abstractions.Wallets;
using NBitcoin;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionNativePromptTests
{
    [Fact]
    public async Task DirectPromptNegotiatesLWithoutDerivingARegularPaymentOrBoardingContract()
    {
        await using var database = await CompositionDatabase.Create();
        var service = Service(database);
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, null!, service);
        var store = ArkCompositionPromptTests.Store();
        var invoice = Invoice();
        var context = new PaymentMethodContext(store, new StoreBlob(), store.GetPaymentMethodConfigs()[handler.PaymentMethodId],
            handler, invoice, new InvoiceLogs());
        await handler.BeforeFetchingRates(context);

        await handler.ConfigurePrompt(context);

        var route = Assert.Single(await database.Repository.List("store", "invoice", "ARKADE"));
        Assert.Equal(route.CustomerDestination, context.Prompt.Destination);
        Assert.Equal(route.RouteId.ToString(), context.Prompt.Details["compositionRouteId"]!.ToString());
        Assert.Empty(context.TrackedDestinations);
        Assert.Null(handler.ParsePaymentPromptDetails(context.Prompt.Details).GetContract(Network.RegTest));
        Assert.Equal(InvoiceStatus.New, invoice.Status);
    }

    [Fact]
    public async Task StoreScopedLightningPromptUsesIndependentCompositionAndNeverClaimsReceipt()
    {
        await using var database = await CompositionDatabase.Create();
        var client = Client(Service(database));
        var first = await client.CreateInvoice(LightMoney.Satoshis(1000), "order", TimeSpan.FromMinutes(5));
        var second = await client.CreateInvoice(LightMoney.Satoshis(1000), "renewal", TimeSpan.FromMinutes(5));

        Assert.NotEqual(first.PaymentHash, second.PaymentHash);
        Assert.Equal(LightMoney.Satoshis(1025), first.Amount);
        Assert.Equal(LightningInvoiceStatus.Unpaid, first.Status);
        Assert.Null(first.Preimage);
        Assert.Equal(2, (await database.Repository.List("store", paymentMethodId: "BTC-LN")).Count);
        Assert.Equal(first.BOLT11, (await client.GetInvoice(first.Id))!.BOLT11);
        Assert.Equal(first.Id, (await client.GetInvoice(new uint256(first.PaymentHash)))!.Id);
        Assert.Equal(2, (await client.ListInvoices()).Length);
    }

    [Fact]
    public async Task VerifiedEvmDeliveryMarksComposedLightningInvoicePaidInQueries()
    {
        await using var database = await CompositionDatabase.Create();
        var service = Service(database);
        var client = Client(service);
        var invoice = await client.CreateInvoice(LightMoney.Satoshis(1000), "order", TimeSpan.FromMinutes(5));
        var route = Assert.Single(await database.Repository.List("store", paymentMethodId: "BTC-LN"));
        var revision = route.Revision;
        route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 1000);
        route.RecordIngressClaimedToOutgoing(CompositionFixture.ClaimTx);
        route.RecordOutgoingLockupFunded(CompositionFixture.ClaimTx, 1000);
        route.RecordEvmLockProven(CompositionFixture.LockProof());
        route.RecordEvmClaimVerified(CompositionFixture.EvmClaimTx, "2000000");
        await database.Repository.Save("store", route, revision);

        Assert.Equal(LightningInvoiceStatus.Paid, (await client.GetInvoice(invoice.Id))!.Status);
        Assert.Equal(LightningInvoiceStatus.Paid,
            Assert.Single(await client.ListInvoices(new ListInvoicesParams())).Status);
        Assert.Empty(await client.ListInvoices(new ListInvoicesParams { PendingOnly = true }));
    }

    [Fact]
    public async Task WatchOnlyComposedLightningPromptDoesNotRequireSpendCapability()
    {
        await using var database = await CompositionDatabase.Create();

        var invoice = await Client(Service(database), spendAuthorized: false)
            .CreateInvoice(LightMoney.Satoshis(1000), "order", TimeSpan.FromMinutes(5));

        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        Assert.Single(await database.Repository.List("store", paymentMethodId: "BTC-LN"));
    }

    [Fact]
    public async Task OrdinaryLightningReceiveStillRequiresSpendCapability()
    {
        await using var database = await CompositionDatabase.Create();

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Client(Service(database), storeId: null, spendAuthorized: false)
                .CreateInvoice(LightMoney.Satoshis(1000), "order", TimeSpan.FromMinutes(5)));

        Assert.Contains("not authorised to spend", error.Message);
    }

    [Fact]
    public async Task FractionalSatoshiLightningAmountIsRejectedBeforeCreatingARoute()
    {
        await using var database = await CompositionDatabase.Create();
        var client = Client(Service(database));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateInvoice(
            LightMoney.MilliSatoshis(1_000_001), "fractional", TimeSpan.FromMinutes(5)));

        Assert.Contains("whole satoshis", error.Message);
        Assert.Empty(await database.Repository.List("store"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("store")]
    public async Task LegacyOrDisabledLightningStillUsesTheExistingCorridor(string? storeId)
    {
        await using var database = await CompositionDatabase.Create();
        var service = new ArkCompositionPromptService(database.Repository, new PromptExecutor(database.Repository),
            contextSource: TestProxy.Create<IArkCompositionContextSource>((_, _) => Task.FromResult<StoreData?>(
                ArkCompositionPromptTests.Store(CompositionFixture.Settings() with
                {
                    RoutePolicy = CompositionFixture.Settings().RoutePolicy! with { EnabledSourceRails = ["ARKADE"] }
                }))), executionLock: PromptExecutionLock.Available);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(service, storeId)
            .CreateInvoice(LightMoney.Satoshis(1000), "legacy", TimeSpan.FromMinutes(5)));
        Assert.Contains("Arkade Lightning corridors are not configured", error.Message);
        Assert.Empty(await database.Repository.List("store"));
    }

    internal static InvoiceEntity Invoice()
    {
        var invoice = new InvoiceEntity { Id = "invoice", StoreId = "store", Currency = "BTC", Price = 0.00001m,
            ExpirationTime = ArkCompositionPromptTests.Expiry, Status = InvoiceStatus.New };
        invoice.UpdateTotals();
        return invoice;
    }

    internal static ArkCompositionPromptService Service(CompositionDatabase database, IArkCompositionExecutionLock? executionLock = null,
        IArkCompositionExecutor? executor = null, InvoiceEntity? invoice = null) => new(database.Repository, executor ?? new PromptExecutor(database.Repository), contextSource: TestProxy.Create<IArkCompositionContextSource>((method, args) => method.Name switch
        {
            nameof(IArkCompositionContextSource.FindStoreAsync) => Task.FromResult<StoreData?>(args![0] as string == "store" ? ArkCompositionPromptTests.Store() : null),
            nameof(IArkCompositionContextSource.FindInvoiceAsync) => Task.FromResult<InvoiceEntity?>(args![0] as string == "invoice" ? invoice ?? Invoice() : null),
            _ => throw new NotSupportedException(method.Name)
        }), executionLock: executionLock ?? PromptExecutionLock.Available);

    internal static ArkLightningClient Client(ArkCompositionPromptService service, string? storeId = "store",
        bool spendAuthorized = true)
    {
        var storage = TestProxy.Create<IWalletStorage>((method, _) => method.Name == nameof(IWalletStorage.GetWalletById)
            ? Task.FromResult<ArkWalletInfo?>(new ArkWalletInfo("private-wallet", null, null, WalletType.SingleKey, null, 0,
                spendAuthorized ? new Dictionary<string, string> { [ArkLightningClient.SpendKeyMetadataKey] = "capability" } : []))
            : throw new NotSupportedException(method.Name));
        return new ArkLightningClient(null!, Network.RegTest, "private-wallet", null!, null!,
            NullLogger<ArkLightningInvoiceListener>.Instance, new ArkLightningSpendCapability(spendAuthorized ? "capability" : null),
            new ArkLightningSpendKeyService(storage), storeContext: new ArkLightningStoreContext(storeId), compositionPrompts: service);
    }
}
