using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Data;
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
    public void CompositionIsDurableAndKeyedByStoreAndInvoiceWithoutSecretColumns()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
        var entity = context.Model.FindEntityType(typeof(ArkInvoiceComposition));

        Assert.NotNull(entity);
        Assert.Equal("BTCPayServer.Plugins.Ark", entity.GetSchema());
        Assert.Equal(["StoreId", "InvoiceId"], entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "AssetId", "CreatedAt", "Destination", "InvoiceId", "Status", "StoreId", "WalletId" },
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
        var createdAt = DateTimeOffset.UtcNow;

        var composition = ArkInvoiceComposition.Create(store, invoice, handlers, createdAt);

        Assert.Equal(store.Id, composition.StoreId);
        Assert.Equal(invoice.Id, composition.InvoiceId);
        Assert.Equal("wallet-identifier-not-for-api", composition.WalletId);
        Assert.Equal(Asset, composition.AssetId);
        Assert.Equal(Destination, composition.Destination);
        Assert.Equal(createdAt, composition.CreatedAt);
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
