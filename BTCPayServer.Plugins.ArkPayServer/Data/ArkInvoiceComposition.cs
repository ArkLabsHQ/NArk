using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

/// <summary>One independently offered payment route and its public recovery journal.</summary>
public sealed partial class ArkInvoiceComposition
{
    private ArkInvoiceComposition() { }

    /// <summary>Independent identity for this rail and renewal.</summary>
    public Guid RouteId { get; private set; }
    /// <summary>Immutable owning store.</summary>
    public string StoreId { get; private set; } = "";
    /// <summary>Invoice attached by exact store, rail and hash when available.</summary>
    public string? InvoiceId { get; private set; }
    /// <summary>ARKADE, BTC-LN or BTC-CHAIN.</summary>
    public string PaymentMethodId { get; private set; } = "";
    /// <summary>SDK-generated H, globally unique per route; never P.</summary>
    public string? PaymentHash { get; private set; }
    /// <summary>Private wallet binding, excluded from API serialization.</summary>
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string WalletId { get; private set; } = "";
    /// <summary>Immutable CAIP-19 chain/token identity.</summary>
    public string AssetId { get; private set; } = "";
    /// <summary>Immutable merchant destination.</summary>
    public string Destination { get; private set; } = "";
    /// <summary>UTC route creation time.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Monotonic observed-money state; no customer-source event advances it.</summary>
    public string Status { get; private set; } = "PendingSdk";

    /// <summary>Records the SDK's public hash once; the preimage remains in SDK storage.</summary>
    public void AssignPaymentHash(string paymentHash)
    {
        var hash = NormalizeHash(paymentHash);
        if (PaymentHash is not null && PaymentHash != hash)
            throw new InvalidOperationException("A route cannot change its payment hash.");
        if (PaymentHash == hash) return;
        Revision = checked(Revision + 1);
        PaymentHash = hash;
    }

    /// <summary>Attaches an exact hash lookup without treating ingress funding as EVM settlement.</summary>
    public void AttachInvoice(InvoiceEntity invoice, PaymentMethodId paymentMethodId, string paymentHash)
    {
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.Id) || invoice.StoreId != StoreId ||
            NormalizePaymentMethod(paymentMethodId) != PaymentMethodId || NormalizeHash(paymentHash) != PaymentHash ||
            InvoiceId is not null && InvoiceId != invoice.Id)
            throw new InvalidOperationException("Invoice attachment does not match this route.");
        if (InvoiceId == invoice.Id) return;
        Revision = checked(Revision + 1);
        InvoiceId = invoice.Id;
    }

    /// <summary>Creates a distinct inert route, snapshotting only public store policy and wallet ownership.</summary>
    public static ArkInvoiceComposition Create(StoreData store, InvoiceEntity? invoice,
        PaymentMethodHandlerDictionary handlers, DateTimeOffset createdAt, PaymentMethodId? paymentMethodId = null)
        => Create(store, invoice, store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, handlers),
            createdAt, paymentMethodId);

    internal static ArkInvoiceComposition Create(StoreData store, InvoiceEntity? invoice,
        ArkadePaymentMethodConfig? configuration, DateTimeOffset createdAt, PaymentMethodId? paymentMethodId = null)
    {
        if (string.IsNullOrWhiteSpace(store.Id) || invoice is not null &&
            (string.IsNullOrWhiteSpace(invoice.Id) || !string.Equals(store.Id, invoice.StoreId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Invoice does not belong to this store.");
        if (string.IsNullOrWhiteSpace(configuration?.WalletId) || configuration.EvmSettlement?.Enabled != true)
            throw new InvalidOperationException("EVM settlement is not enabled for this store.");
        var settings = configuration.EvmSettlement.Validate();
        var rail = NormalizePaymentMethod(paymentMethodId ?? ArkadePlugin.ArkadePaymentMethodId);
        if (settings.RoutePolicy is not null && !settings.RoutePolicy.EnabledSourceRails.Contains(rail))
            throw new InvalidOperationException("The source payment method is disabled for this store.");
        return new ArkInvoiceComposition
        {
            RouteId = Guid.NewGuid(),
            StoreId = store.Id,
            InvoiceId = invoice?.Id,
            PaymentMethodId = rail,
            WalletId = configuration.WalletId,
            AssetId = settings.AssetId,
            Destination = settings.Destination,
            SwapContractAddress = settings.RoutePolicy?.SwapContractAddress,
            CreatedAt = new DateTimeOffset(createdAt.UtcTicks - createdAt.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero)
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
        var rail = value.ToUpperInvariant();
        if (rail is not ("ARKADE" or "BTC-LN" or "BTC-CHAIN"))
            throw new ArgumentException("Specify a supported source payment method.", nameof(paymentMethodId));
        return rail;
    }
}
