using System.Net;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public partial class ArkEvmSettlementApiTests
{
    [Fact]
    public async Task OnchainPromptRejectsUnsafeExecutionLockWithoutCreatingARoute()
    {
        await using var database = await CompositionDatabase.Create();
        var invoice = PayableOnchainInvoice();
        await using var app = CreateHost(new ArkadePaymentMethodConfig("private-wallet") { EvmSettlement = CompositionFixture.Settings() },
            repository: database.Repository, prompts: ArkCompositionNativePromptTests.Service(database,
                new PromptExecutionLock(false), invoice: invoice));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanCreateInvoice);

        using var response = await client.PostAsync("/api/v1/stores/store/arkade/evm-settlement/invoices/invoice/onchain-prompt", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("cross-process-execution-lock-unavailable", JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("code"));
        Assert.Empty(await database.Repository.List("store"));
    }

    [Fact]
    public async Task OnchainPromptCreatesAnIndependentOwnedRouteAndCanBeReadWithoutSecrets()
    {
        await using var database = await CompositionDatabase.Create();
        var executor = new PromptExecutor(database.Repository);
        var executionLock = PromptExecutionLock.Available;
        var invoice = PayableOnchainInvoice(25);
        await using var app = CreateHost(new ArkadePaymentMethodConfig("private-wallet") { EvmSettlement = CompositionFixture.Settings() },
            repository: database.Repository, prompts: ArkCompositionNativePromptTests.Service(database, executionLock, executor, invoice),
            compositionExecutor: executor, executionLock: executionLock);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanCreateInvoice);
        using var response = await client.PostAsync("/api/v1/stores/store/arkade/evm-settlement/invoices/invoice/onchain-prompt", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BTC-CHAIN", created.Value<string>("paymentMethodId"));
        Assert.Equal("invoice", created.Value<string>("invoiceId"));
        Assert.Equal("bcrt1ppublichtlc", created.Value<string>("customerDestination"));
        Assert.Equal(1800000020, created.Value<long>("checkoutExpiresAt"));
        Assert.False(created.Value<bool>("settlementVerified"));
        Assert.DoesNotContain("private-wallet", created.ToString());
        Assert.DoesNotContain("preimage", created.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1000, (await database.Repository.List("store", "invoice", "BTC-CHAIN")).Single().BaseAmountSats);
        client.DefaultRequestHeaders.Remove("Test-Permission");
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewInvoices);
        var loaded = JObject.Parse(await client.GetStringAsync(response.Headers.Location));
        Assert.True(JToken.DeepEquals(created, loaded));
        Assert.Single(await database.Repository.List("store", "invoice", "BTC-CHAIN"));
    }

    [Theory]
    [InlineData(null, "store", "invoice", HttpStatusCode.Unauthorized)]
    [InlineData(Policies.CanViewInvoices, "store", "invoice", HttpStatusCode.Forbidden)]
    [InlineData(Policies.CanCreateInvoice, "other", "invoice", HttpStatusCode.NotFound)]
    [InlineData(Policies.CanCreateInvoice, "store", "absent", HttpStatusCode.NotFound)]
    public async Task OnchainPromptRequiresOwningCreatePermission(string? permission, string store, string invoice, HttpStatusCode expected)
    {
        await using var database = await CompositionDatabase.Create();
        await using var app = CreateHost(new ArkadePaymentMethodConfig("private-wallet") { EvmSettlement = CompositionFixture.Settings() },
            repository: database.Repository, prompts: ArkCompositionNativePromptTests.Service(database));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        if (permission is not null) client.DefaultRequestHeaders.Add("Test-Permission", permission);
        using var response = await client.PostAsync($"/api/v1/stores/{store}/arkade/evm-settlement/invoices/{invoice}/onchain-prompt", null);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(await database.Repository.List("store"));
    }

    [Fact]
    public async Task OnchainPromptRejectsInvoiceWithoutAnActiveBtcChainPrompt()
    {
        await using var database = await CompositionDatabase.Create();
        await using var app = CreateHost(new ArkadePaymentMethodConfig("private-wallet") { EvmSettlement = CompositionFixture.Settings() },
            repository: database.Repository, prompts: ArkCompositionNativePromptTests.Service(database));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanCreateInvoice);

        using var response = await client.PostAsync("/api/v1/stores/store/arkade/evm-settlement/invoices/invoice/onchain-prompt", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("payment-method-not-available",
            JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("code"));
        Assert.Empty(await database.Repository.List("store"));
    }

    private static BTCPayServer.Services.Invoices.InvoiceEntity PayableOnchainInvoice(long feeSats = 0)
    {
        var invoice = ArkCompositionNativePromptTests.Invoice();
        invoice.SetPaymentPrompt(ArkCompositionOnchainPromptTests.Onchain, new BTCPayServer.Services.Invoices.PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, PaymentMethodFee = Money.Satoshis(feeSats).ToDecimal(MoneyUnit.BTC),
            Destination = "merchant-wallet-address", Details = new JObject()
        });
        return invoice;
    }
}
