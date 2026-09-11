using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using DesignTimeDbContextFactory = BTCPayServer.Plugins.ArkPayServer.Data.DesignTimeDbContextFactory;

namespace NArk.Tests;

public class ArkInvoiceCompositionTests
{
    private const string Asset = "eip155:42161/erc20:0x1111111111111111111111111111111111111111";
    private const string Destination = "0x2222222222222222222222222222222222222222";

    [Fact]
    public void CompositionAllowsIndependentRoutesAndExactHashAttachmentWithoutSecretColumns()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
        var entity = context.Model.FindEntityType(typeof(ArkInvoiceComposition));

        Assert.NotNull(entity);
        Assert.Equal("BTCPayServer.Plugins.Ark", entity.GetSchema());
        Assert.Equal(["RouteId"], entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.True(entity.FindProperty("InvoiceId")!.IsNullable);
        Assert.True(entity.FindProperty("PaymentHash")!.IsNullable);
        Assert.True(entity.FindProperty("PaymentHash")!.IsConcurrencyToken);
        Assert.True(entity.FindProperty("InvoiceId")!.IsConcurrencyToken);
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["PaymentHash"]));
        Assert.Contains(entity.GetIndexes(), i => !i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["StoreId", "InvoiceId", "PaymentMethodId"]));
        Assert.Equal(new[] { "AssetId", "BaseAmountSats", "CheckoutExpiresAt", "CreatedAt", "CustomerDestination", "Destination", "EvmAmount", "EvmClaimAddress",
                "EvmClaimTransactionId", "EvmLockTransactionId", "EvmObservedAtBlock", "EvmProvenAtBlock", "EvmProvenBlockTimestamp",
                "EvmRefundAddress", "EvmTimeoutBlock", "EvmTokenAddress", "FailureCode", "IngressClaimTransactionId", "InvoiceId",
                "PaymentHash", "PaymentMethodId", "Revision", "RouteId", "Status", "StoreId", "SwapContractAddress", "WalletId" },
            entity.GetProperties().Select(p => p.Name).Order());
    }

    [Fact]
    public void CompositionSnapshotsOnlyApprovedConfigurationAndNeverInvoiceSecrets()
    {
        var (store, handlers) = ConfiguredStore();
        var invoice = new InvoiceEntity
        {
            Id = "invoice",
            StoreId = store.Id,
            Metadata = new InvoiceMetadata
            {
                AdditionalData = new Dictionary<string, JToken>
                {
                    ["preimage"] = "secret-preimage",
                    ["descriptor"] = "secret-descriptor"
                }
            }
        };
        var createdAt = new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);

        var composition = ArkInvoiceComposition.Create(store, invoice, handlers, createdAt);

        Assert.Equal(store.Id, composition.StoreId);
        Assert.Equal(invoice.Id, composition.InvoiceId);
        Assert.Equal("wallet-identifier-not-for-api", composition.WalletId);
        Assert.Equal(Asset, composition.AssetId);
        Assert.Equal(Destination, composition.Destination);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero).AddTicks(1234560), composition.CreatedAt);
        Assert.Equal("PendingSdk", composition.Status);
        foreach (var json in new[] { JsonConvert.SerializeObject(composition), System.Text.Json.JsonSerializer.Serialize(composition) })
        {
            Assert.DoesNotContain("secret-", json);
            Assert.DoesNotContain("wallet-identifier", json);
        }
    }

    [Fact]
    public void CompositionRejectsAnInvoiceFromAnotherStore()
    {
        var (store, handlers) = ConfiguredStore();
        var invoice = new InvoiceEntity { Id = "private-invoice", StoreId = "other" };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ArkInvoiceComposition.Create(store, invoice, handlers, DateTimeOffset.UtcNow));

        Assert.DoesNotContain(invoice.Id, error.Message);
    }

    [Fact]
    public void CompositionRequiresExplicitlyEnabledSettlement()
    {
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, null!);
        var store = new StoreData { Id = "store" };
        store.SetPaymentMethodConfig(handler, new ArkadePaymentMethodConfig("wallet"));

        Assert.Throws<InvalidOperationException>(() => ArkInvoiceComposition.Create(store,
            new InvoiceEntity { Id = "invoice", StoreId = store.Id }, new PaymentMethodHandlerDictionary([handler]), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AlternativeRailsAndRenewalsHaveIndependentRouteIdentities()
    {
        var (store, handlers) = ConfiguredStore();
        var invoice = new InvoiceEntity { Id = "invoice", StoreId = store.Id };
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
        var routes = new[] { "ARKADE", "BTC-LN", "BTC-CHAIN", "BTC-LN" }.Select(rail =>
            ArkInvoiceComposition.Create(store, invoice, handlers, DateTimeOffset.UtcNow, PaymentMethodId.Parse(rail))).ToArray();

        context.InvoiceCompositions.AddRange(routes);

        Assert.Equal(4, routes.Select(r => r.RouteId).Distinct().Count());
        Assert.All(routes, route => Assert.Null(route.PaymentHash));
        Assert.Equal(new[] { "ARKADE", "BTC-LN", "BTC-CHAIN", "BTC-LN" }, routes.Select(r => r.PaymentMethodId));
    }

    [Fact]
    public void LightningRouteCanExistBeforeBtcpayPersistsItsInvoice()
    {
        var (store, handlers) = ConfiguredStore();

        var route = ArkInvoiceComposition.Create(store, null, handlers, DateTimeOffset.UtcNow, PaymentMethodId.Parse("BTC-LN"));

        Assert.Null(route.InvoiceId);
        Assert.Equal(store.Id, route.StoreId);
        Assert.Equal("PendingSdk", route.Status);
    }

    [Fact]
    public void HashAssignmentIsCanonicalAndImmutable()
    {
        var (store, handlers) = ConfiguredStore();
        var route = ArkInvoiceComposition.Create(store, null, handlers, DateTimeOffset.UtcNow);
        var hash = new string('a', 64);

        route.AssignPaymentHash(hash.ToUpperInvariant());
        route.AssignPaymentHash(hash);

        Assert.Equal(hash, route.PaymentHash);
        Assert.Throws<InvalidOperationException>(() => route.AssignPaymentHash(new string('b', 64)));
        Assert.Equal(hash, route.PaymentHash);
        Assert.Equal("PendingSdk", route.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret-preimage")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void HashAssignmentRejectsMalformedInputWithoutEchoingIt(string? input)
    {
        var (store, handlers) = ConfiguredStore();
        var route = ArkInvoiceComposition.Create(store, null, handlers, DateTimeOffset.UtcNow);

        var error = Assert.Throws<ArgumentException>(() => route.AssignPaymentHash(input!));

        Assert.Null(route.PaymentHash);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void ExactInvoiceAttachmentIsIdempotentAndDoesNotCompletePayment()
    {
        var (store, handlers) = ConfiguredStore();
        var rail = PaymentMethodId.Parse("BTC-LN");
        var route = ArkInvoiceComposition.Create(store, null, handlers, DateTimeOffset.UtcNow, rail);
        var hash = new string('a', 64);
        route.AssignPaymentHash(hash);
        var invoice = new InvoiceEntity { Id = "invoice", StoreId = store.Id };

        route.AttachInvoice(invoice, rail, hash);
        route.AttachInvoice(invoice, rail, hash.ToUpperInvariant());

        Assert.Equal(invoice.Id, route.InvoiceId);
        Assert.Equal("PendingSdk", route.Status);
    }

    [Theory]
    [InlineData("other-store", "BTC-LN", "a", "invoice")]
    [InlineData("store", "BTC-CHAIN", "a", "invoice")]
    [InlineData("store", "BTC-LN", "b", "invoice")]
    [InlineData("store", "BTC-LN", "a", "replacement-invoice")]
    public void AttachmentCannotChangeStoreRailHashOrExistingInvoice(string storeId, string rail, string hashCharacter, string invoiceId)
    {
        var (store, handlers) = ConfiguredStore();
        var route = ArkInvoiceComposition.Create(store, new InvoiceEntity { Id = "invoice", StoreId = store.Id },
            handlers, DateTimeOffset.UtcNow, PaymentMethodId.Parse("BTC-LN"));
        route.AssignPaymentHash(new string('a', 64));

        Assert.Throws<InvalidOperationException>(() => route.AttachInvoice(
            new InvoiceEntity { Id = invoiceId, StoreId = storeId }, PaymentMethodId.Parse(rail), new string(hashCharacter[0], 64)));

        Assert.Equal("invoice", route.InvoiceId);
    }

    [Fact]
    public void AttachmentRequiresAnAssignedHash()
    {
        var (store, handlers) = ConfiguredStore();
        var route = ArkInvoiceComposition.Create(store, null, handlers, DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => route.AttachInvoice(
            new InvoiceEntity { Id = "invoice", StoreId = store.Id }, PaymentMethodId.Parse("ARKADE"), new string('a', 64)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret invalid rail")]
    public void RouteRejectsMalformedPaymentMethodIdentity(string rail)
    {
        var (store, handlers) = ConfiguredStore();

        var error = Assert.Throws<ArgumentException>(() => ArkInvoiceComposition.Create(
            store, null, handlers, DateTimeOffset.UtcNow, new PaymentMethodId(rail)));

        Assert.DoesNotContain("secret", error.Message);
    }

    private static (StoreData Store, PaymentMethodHandlerDictionary Handlers) ConfiguredStore()
    {
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, null!);
        var store = new StoreData { Id = "store" };
        store.SetPaymentMethodConfig(handler, new ArkadePaymentMethodConfig("wallet-identifier-not-for-api")
        {
            EvmSettlement = new ArkEvmSettlementSettings(Asset, Destination, true)
        });
        return (store, new PaymentMethodHandlerDictionary([handler]));
    }
}
