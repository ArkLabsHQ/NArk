using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public sealed class ArkInvoiceComposition
{
    private ArkInvoiceComposition() { }

    public Guid RouteId { get; private set; }
    public string StoreId { get; private set; } = "";
    public string? InvoiceId { get; private set; }
    public string PaymentMethodId { get; private set; } = "";
    public string? PaymentHash { get; private set; }
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string WalletId { get; private set; } = "";
    public string AssetId { get; private set; } = "";
    public string Destination { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public string Status { get; private set; } = "PendingSdk";

    /// <summary>Records the SDK's public hash once; the preimage remains in SDK storage.</summary>
    public void AssignPaymentHash(string paymentHash)
    {
        var hash = NormalizeHash(paymentHash);
        if (PaymentHash is not null && PaymentHash != hash)
            throw new InvalidOperationException("A route cannot change its payment hash.");
        PaymentHash = hash;
    }

    /// <summary>Attaches an exact hash lookup without treating ingress funding as EVM settlement.</summary>
    public void AttachInvoice(InvoiceEntity invoice, PaymentMethodId paymentMethodId, string paymentHash)
    {
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.Id) || invoice.StoreId != StoreId ||
            NormalizePaymentMethod(paymentMethodId) != PaymentMethodId || NormalizeHash(paymentHash) != PaymentHash ||
            InvoiceId is not null && InvoiceId != invoice.Id)
            throw new InvalidOperationException("Invoice attachment does not match this route.");
        InvoiceId = invoice.Id;
    }

    public static ArkInvoiceComposition Create(StoreData store, InvoiceEntity? invoice,
        PaymentMethodHandlerDictionary handlers, DateTimeOffset createdAt, PaymentMethodId? paymentMethodId = null)
    {
        if (string.IsNullOrWhiteSpace(store.Id) || invoice is not null &&
            (string.IsNullOrWhiteSpace(invoice.Id) || !string.Equals(store.Id, invoice.StoreId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Invoice does not belong to this store.");
        var configuration = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, handlers);
        if (string.IsNullOrWhiteSpace(configuration?.WalletId) || configuration.EvmSettlement?.Enabled != true)
            throw new InvalidOperationException("EVM settlement is not enabled for this store.");
        var settings = configuration.EvmSettlement.Validate();
        return new ArkInvoiceComposition
        {
            RouteId = Guid.NewGuid(),
            StoreId = store.Id,
            InvoiceId = invoice?.Id,
            PaymentMethodId = NormalizePaymentMethod(paymentMethodId ?? ArkadePlugin.ArkadePaymentMethodId),
            WalletId = configuration.WalletId,
            AssetId = settings.AssetId,
            Destination = settings.Destination,
            CreatedAt = createdAt.ToUniversalTime()
        };
    }

    private static string NormalizeHash(string paymentHash)
    {
        if (paymentHash is null || paymentHash.Length != 64 || !paymentHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Specify a 32-byte payment hash.", nameof(paymentHash));
        return paymentHash.ToLowerInvariant();
    }

    private static string NormalizePaymentMethod(PaymentMethodId paymentMethodId)
    {
        var value = paymentMethodId?.ToString();
        if (string.IsNullOrEmpty(value) || value.Length > 50 ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || value.StartsWith('-') || value.EndsWith('-'))
            throw new ArgumentException("Specify a valid payment method.", nameof(paymentMethodId));
        return value.ToUpperInvariant();
    }
}
