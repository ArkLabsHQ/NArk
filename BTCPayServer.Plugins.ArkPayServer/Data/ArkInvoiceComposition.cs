using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public sealed class ArkInvoiceComposition
{
    private ArkInvoiceComposition() { }

    public string StoreId { get; private set; } = "";
    public string InvoiceId { get; private set; } = "";
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string WalletId { get; private set; } = "";
    public string AssetId { get; private set; } = "";
    public string Destination { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public string Status { get; private set; } = "PendingSdk";

    public static ArkInvoiceComposition Create(StoreData store, InvoiceEntity invoice,
        PaymentMethodHandlerDictionary handlers, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(store.Id) || string.IsNullOrWhiteSpace(invoice.Id) ||
            !string.Equals(store.Id, invoice.StoreId, StringComparison.Ordinal))
            throw new InvalidOperationException("Invoice does not belong to this store.");
        var configuration = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, handlers);
        if (string.IsNullOrWhiteSpace(configuration?.WalletId) || configuration.EvmSettlement?.Enabled != true)
            throw new InvalidOperationException("EVM settlement is not enabled for this store.");
        var settings = configuration.EvmSettlement.Validate();
        return new ArkInvoiceComposition
        {
            StoreId = store.Id,
            InvoiceId = invoice.Id,
            WalletId = configuration.WalletId,
            AssetId = settings.AssetId,
            Destination = settings.Destination,
            CreatedAt = createdAt.ToUniversalTime()
        };
    }
}
