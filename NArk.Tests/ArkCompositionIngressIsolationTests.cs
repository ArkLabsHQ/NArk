using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.Storage.EfCore.Entities;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionIngressIsolationTests
{
    [Fact]
    public async Task ComposedPromptDoesNotActivateNormalInvoiceContractTracking()
    {
        var invoice = ArkCompositionNativePromptTests.Invoice();
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, null!);
        invoice.SetPaymentPrompt(handler.PaymentMethodId, new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8,
            Details = JObject.FromObject(new ArkadePromptDetails { CompositionRouteId = Guid.NewGuid() }, handler.Serializer)
        });
        var listener = new ArkContractInvoiceListener(null!, null!, handler, null!, null!, null!, null!, null!,
            NullLogger<ArkContractInvoiceListener>.Instance);

        await listener.ToggleArkadeContract(invoice);

        Assert.Empty(invoice.GetPayments(false));
        Assert.Equal(BTCPayServer.Client.Models.InvoiceStatus.New, invoice.Status);
    }

    [Fact]
    public async Task FulfilledIngressIsSuppressedButLegacyFulfilledInvoiceStillNotifiesPaid()
    {
        await using var database = await CompositionDatabase.Create();
        var service = ArkCompositionNativePromptTests.Service(database);
        var route = (await service.TryCreateAsync(ArkCompositionPromptTests.Store(), null, "BTC-LN", 1000, ArkCompositionPromptTests.Expiry))!;
        var composed = Intent(route.Legs.Single(l => l.Kind == "Ingress").RfqId, route.PaymentHash!);
        var legacy = Intent(new string('f', 64), new string('e', 64));
        Assert.Equal(LightningInvoiceStatus.Paid, ArkadeIntentLightningMapper.ToInvoice(composed, Network.RegTest)!.Status);
        EventHandler<ArkadeSwapIntent>? changed = null;
        var storage = TestProxy.Create<IArkadeIntentStorage>((method, args) =>
        {
            if (method.Name == "add_SwapsChanged") changed += (EventHandler<ArkadeSwapIntent>)args![0]!;
            else if (method.Name == "remove_SwapsChanged") changed -= (EventHandler<ArkadeSwapIntent>)args![0]!;
            else throw new NotSupportedException(method.Name);
            return null;
        });
        using var listener = new ArkLightningInvoiceListener("private-wallet", NullLogger<ArkLightningInvoiceListener>.Instance,
            storage, Network.RegTest, CancellationToken.None, service);
        changed!(null, composed);
        changed!(null, legacy);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var paid = await listener.WaitInvoice(timeout.Token);
        Assert.Equal(legacy.Id, paid!.Id);
        using var noMore = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Assert.NotEqual(LightningInvoiceStatus.Paid, (await listener.WaitInvoice(noMore.Token))!.Status);
        Assert.False((await database.Repository.Get("store", route.RouteId))!.SettlementVerified);
    }

    internal static ArkadeSwapIntent Intent(string id, string hash) => new ArkadeSwapIntent
    {
        Id = id, WalletId = "private-wallet", Type = ArkadeSwapIntentType.LightningToBtc, Status = ArkadeSwapIntentStatus.Fulfilled,
        CreatedAt = DateTimeOffset.UtcNow, OfferAmount = Money.Satoshis(1025), WantAmount = Money.Satoshis(1000),
        SwapPkScript = CompositionFixture.MScript, SwapAddress = CompositionFixture.IngressQuote().LockupAddress,
        PaymentHash = hash
    }.WithLightningMetadata(new LightningSwapMetadata(
        "lnbcrt100u1pd2e6uspp5ajnadvhazjrz55twd5k6yeg9u87wpw0q2fdr7g960yl5asv5fmnqdq9d3hkccqpxmedyrk0ehw5ueqx5e0r4qrrv74cewddfcvsxaawqz7634cmjj39sqwy5tvhz0hasktkk6t9pqfdh3edmf3z09zst5y7khv3rvxh8ctqqw6mwhh",
        new string('1', 64)));
}
