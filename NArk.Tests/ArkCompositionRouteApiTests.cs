using System.Net;
using BTCPayServer.Client;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public partial class ArkEvmSettlementApiTests
{
    [Theory]
    [InlineData("", null, HttpStatusCode.Unauthorized)]
    [InlineData("/00000000-0000-0000-0000-000000000001", null, HttpStatusCode.Unauthorized)]
    [InlineData("", Policies.CanViewStoreSettings, HttpStatusCode.Forbidden)]
    [InlineData("/00000000-0000-0000-0000-000000000001", Policies.CanModifyStoreSettings, HttpStatusCode.Forbidden)]
    [InlineData("", "unrelated", HttpStatusCode.Forbidden)]
    public async Task RouteReadsRequireGreenfieldInvoicePermission(string suffix, string? permission, HttpStatusCode status)
    {
        await using var database = await CompositionDatabase.Create();
        await using var app = CreateHost(repository: database.Repository);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        if (permission is not null) client.DefaultRequestHeaders.Add("Test-Permission", permission);

        using var response = await client.GetAsync("/api/v1/stores/store/arkade/evm-settlement/routes" + suffix);

        Assert.Equal(status, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnerReadsOnlyExplicitPublicFactsWithBothSerializers(bool newtonsoft)
    {
        await using var database = await CompositionDatabase.Create();
        var route = CompositionFixture.Completed();
        route.AttachInvoice(new InvoiceEntity { Id = "invoice", StoreId = "store" }, PaymentMethodId.Parse("BTC-LN"), CompositionFixture.Hash);
        await database.Repository.Add("store", route);
        var logs = new TestLogSink();
        await using var app = CreateHost(new ArkadePaymentMethodConfig("private-wallet")
        {
            EvmSettlement = CompositionFixture.Settings() with { ProtectedRpcUri = "secret-ciphertext-sentinel" }
        }, newtonsoft, logs, database.Repository);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewInvoices);

        var body = await client.GetStringAsync($"/api/v1/stores/store/arkade/evm-settlement/routes/{route.RouteId}");
        var data = JObject.Parse(body);

        Assert.Equal(route.RouteId.ToString(), data.Value<string>("routeId"));
        Assert.Equal("invoice", data.Value<string>("invoiceId"));
        Assert.Equal(CompositionFixture.Hash, data.Value<string>("paymentHash"));
        Assert.Equal("EvmClaimVerified", data.Value<string>("status"));
        Assert.True(data.Value<bool>("settlementVerified"));
        Assert.False(data.Value<bool>("executionAvailable"));
        Assert.Equal("evm-settlement", data.Value<string>("paymentCompletionCondition"));
        Assert.Equal(25, data.Value<long>("ingressFeeSats"));
        Assert.Equal(2, ((JArray)data["legs"]!).Count);
        foreach (var forbidden in new[] { "private-wallet", "preimage", "ciphertext", "protectedRpc", "descriptor", "walletId" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain("private-wallet", string.Join('\n', logs.Messages));
        Assert.DoesNotContain("secret-ciphertext-sentinel", body);
        Assert.DoesNotContain("secret-ciphertext-sentinel", string.Join('\n', logs.Messages));
        var list = JArray.Parse(await client.GetStringAsync("/api/v1/stores/store/arkade/evm-settlement/routes?invoiceId=invoice&paymentMethodId=BTC-LN"));
        Assert.Single(list);
        Assert.True(JToken.DeepEquals(data, list[0]));
    }

    [Fact]
    public async Task RouteReadAndListCannotReplaceAuthenticatedStoreOrExposeOtherStoreIds()
    {
        await using var database = await CompositionDatabase.Create();
        var privateRoute = CompositionFixture.Create(storeId: "other");
        await database.Repository.Add("other", privateRoute);
        await using var app = CreateHost(repository: database.Repository);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewInvoices);
        foreach (var path in new[]
                 {
                     $"/api/v1/stores/store/arkade/evm-settlement/routes/{privateRoute.RouteId}",
                     $"/api/v1/stores/other/arkade/evm-settlement/routes/{privateRoute.RouteId}",
                     "/api/v1/stores/other/arkade/evm-settlement/routes"
                 })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain(privateRoute.RouteId.ToString(), await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(JArray.Parse(await client.GetStringAsync("/api/v1/stores/store/arkade/evm-settlement/routes")));
    }

    [Theory]
    [InlineData("take=101")]
    [InlineData("take=0")]
    [InlineData("skip=-1")]
    [InlineData("paymentMethodId=UNSUPPORTED")]
    public async Task RouteListRejectsUnboundedOrUnknownFilters(string query)
    {
        await using var database = await CompositionDatabase.Create();
        await using var app = CreateHost(repository: database.Repository);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewInvoices);
        using var response = await client.GetAsync("/api/v1/stores/store/arkade/evm-settlement/routes?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IngressFundingResponseCannotReportSettlementOrExposeExecution()
    {
        await using var database = await CompositionDatabase.Create();
        var route = CompositionFixture.Prepared();
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        route.RecordIngressQuote(CompositionFixture.IngressQuote());
        route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 1000);
        await database.Repository.Add("store", route);
        await using var app = CreateHost(repository: database.Repository);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Test-Permission", Policies.CanViewInvoices);
        var data = JObject.Parse(await client.GetStringAsync($"/api/v1/stores/store/arkade/evm-settlement/routes/{route.RouteId}"));
        Assert.Equal("IngressLockupFunded", data.Value<string>("status"));
        Assert.False(data.Value<bool>("settlementVerified"));
        Assert.False(data.Value<bool>("executionAvailable"));
        Assert.Null(data["evmClaimTransactionId"]!.Value<string>());
    }
}
