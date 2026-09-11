using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Lightning;
using NArk.ArkadeIntents.Composition;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>Creates independent public prompt journals without interpreting ingress as settlement.</summary>
public sealed class ArkCompositionPromptService(ArkInvoiceCompositionRepository repository,
    IArkCompositionExecutor? executor = null, TimeProvider? timeProvider = null,
    IArkCompositionContextSource? contextSource = null, IArkCompositionExecutionLock? executionLock = null,
    ArkCompositionCheckoutPolicy? checkoutPolicy = null, ComposedSwapOptions? timingOptions = null)
{
    private readonly ComposedSwapOptions _timingOptions = timingOptions ?? new ComposedSwapOptions();

    internal bool ExecutionAvailable => executor is not null && executionLock?.SupportsCrossProcessExecution == true;

    internal Task<InvoiceEntity?> FindInvoiceAsync(string invoiceId) => contextSource?.FindInvoiceAsync(invoiceId)
        ?? throw new InvalidOperationException("The composition invoice context is unavailable.");

    internal static bool Enabled(StoreData store, string rail) => Configuration(store)?.EvmSettlement is
        { Enabled: true, RoutePolicy: { } policy } && policy.EnabledSourceRails.Contains(rail);

    internal async Task<ArkInvoiceComposition?> TryCreateLightningAsync(string storeId, string walletId, long amountSats,
        TimeSpan expiry, CancellationToken cancellationToken)
    {
        var store = contextSource is null ? null : await contextSource.FindStoreAsync(storeId);
        if (store is null) throw new InvalidOperationException("The composition store context is unavailable.");
        return await TryCreateAsync(store, null, "BTC-LN", amountSats,
             (timeProvider ?? TimeProvider.System).GetUtcNow().Add(expiry), walletId, cancellationToken);
    }

    internal async Task AttachLightningAsync(StoreData store, InvoiceEntity invoice, string? lightningInvoiceId,
        string? paymentHash, string? destination, CancellationToken cancellationToken)
    {
        if (store.Id != invoice.StoreId)
            throw new InvalidOperationException("The Lightning prompt invoice does not belong to its store.");
        if (!Guid.TryParseExact(lightningInvoiceId, "N", out var routeId) || string.IsNullOrWhiteSpace(paymentHash)) return;
        var route = await repository.Get(store.Id, routeId, cancellationToken);
        if (route is null) return;
        if (route.PaymentMethodId != "BTC-LN" || route.PaymentHash != paymentHash.ToLowerInvariant() ||
            route.CustomerDestination != destination)
            throw new InvalidOperationException("The Lightning prompt does not match its composed route.");
        var revision = route.Revision;
        route.AttachInvoice(invoice, PaymentMethodId.Parse("BTC-LN"), paymentHash);
        if (route.Revision != revision) await repository.Save(store.Id, route, revision, cancellationToken);
        checkoutPolicy?.RememberStore(store);
        checkoutPolicy?.RememberRoute(route);
    }

    internal async Task<LightningInvoice?> FindLightningAsync(string? storeId, string walletId, string id, CancellationToken cancellationToken)
    {
        if (storeId is null) return null;
        var route = Guid.TryParse(id, out var routeId) ? await repository.Get(storeId, routeId, cancellationToken)
            : id.Length == 64 && id.All(char.IsAsciiHexDigit) ? await repository.FindByPaymentHash(storeId, id, cancellationToken) : null;
        return route is { PaymentMethodId: "BTC-LN", CustomerDestination: not null } && route.WalletId == walletId
            ? ToLightningInvoice(route) : null;
    }

    internal Task<bool> IsCompositionIntentAsync(string walletId, string id, string? paymentHash, CancellationToken cancellationToken) =>
        repository.IsCompositionIntent(walletId, id, paymentHash, cancellationToken);

    internal async Task<IReadOnlyList<LightningInvoice>> ListLightningAsync(string? storeId, string walletId, CancellationToken cancellationToken)
    {
        if (storeId is null) return [];
        var routes = new List<ArkInvoiceComposition>();
        for (var skip = 0; ; skip = checked(skip + 100))
        {
            var page = await repository.List(storeId, paymentMethodId: "BTC-LN", skip: skip, take: 100, cancellationToken: cancellationToken);
            routes.AddRange(page.Where(r => r.WalletId == walletId && r.CustomerDestination is not null));
            if (page.Count < 100) break;
        }
        return routes.OrderByDescending(r => r.CreatedAt).Select(ToLightningInvoice).ToArray();
    }

    internal LightningInvoice ToLightningInvoice(ArkInvoiceComposition route) => new()
    {
        Id = route.RouteId.ToString("N"), PaymentHash = route.PaymentHash, BOLT11 = route.CustomerDestination,
        Amount = LightMoney.Satoshis(checked(route.BaseAmountSats!.Value + route.IngressFeeSats)),
        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(route.CheckoutExpiresAt!.Value),
        Status = route.SettlementVerified ? LightningInvoiceStatus.Paid
            : route.CheckoutExpiresAt <= (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()
                ? LightningInvoiceStatus.Expired : LightningInvoiceStatus.Unpaid
    };

    /// <summary>Returns null for a non-composed rail; enabled routes require an adapter and durable preparation before quoting.</summary>
    public async Task<ArkInvoiceComposition?> TryCreateAsync(StoreData store, InvoiceEntity? invoice, string rail,
        long baseAmountSats, DateTimeOffset expiresAt, string? expectedWalletId = null, CancellationToken cancellationToken = default)
    {
        checkoutPolicy?.RememberStore(store);
        var configuration = Configuration(store);
        if (configuration?.EvmSettlement is not { Enabled: true, RoutePolicy: { } policy } || !policy.EnabledSourceRails.Contains(rail))
            return null;
        if (expectedWalletId is not null && expectedWalletId != configuration.WalletId)
            throw new InvalidOperationException("The Lightning wallet does not match this store's composition policy.");
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (baseAmountSats <= 0 || expiresAt <= now)
            throw new InvalidOperationException("The payment prompt has no payable amount or checkout window.");
        if (executor is null) throw new ArkCompositionUnavailableException("sdk-composition-unavailable");
        if (!ExecutionAvailable)
            throw new ArkCompositionUnavailableException("cross-process-execution-lock-unavailable");
        var route = ArkInvoiceComposition.Create(store, invoice, configuration, now, PaymentMethodId.Parse(rail));
        await repository.Add(store.Id, route, cancellationToken);
        var request = new ArkCompositionExecutionRequest(route.RouteId, route.WalletId, rail, baseAmountSats,
            route.AssetId, route.Destination, policy.Validate()) { StoreId = store.Id };
        var prepared = await InvokeAdapter(() => executor.PrepareAsync(request, cancellationToken), cancellationToken);
        var revision = route.Revision;
        route.Prepare(baseAmountSats, prepared.PaymentHash, prepared.OutgoingRfqId, prepared.IngressRfqId);
        await repository.Save(store.Id, route, revision, cancellationToken);
        var outgoing = await InvokeAdapter(() => executor.QuoteOutgoingAsync(request, prepared, cancellationToken), cancellationToken);
        revision = route.Revision;
        route.RecordOutgoingQuote(outgoing.Quote, outgoing.Terms);
        await repository.Save(store.Id, route, revision, cancellationToken);
        var destination = outgoing.Quote.LockupAddress;
        var requestedExpiry = expiresAt.ToUnixTimeSeconds();
        var quoteExpiry = outgoing.Quote.ValidUntil;
        if (rail != "ARKADE")
        {
            var ingress = await InvokeAdapter(() => executor.QuoteIngressAsync(request, prepared, outgoing.Quote, cancellationToken), cancellationToken);
            revision = route.Revision;
            route.RecordIngressQuote(ingress.Quote);
            await repository.Save(store.Id, route, revision, cancellationToken);
            destination = ingress.CustomerDestination;
            requestedExpiry = Math.Min(requestedExpiry, ingress.ExpiresAt);
            quoteExpiry = Math.Min(quoteExpiry, ingress.Quote.ValidUntil);
        }
        var checkoutExpiry = CheckoutExpiry(requestedExpiry, quoteExpiry);
        revision = route.Revision;
        route.RecordCustomerPrompt(destination, checkoutExpiry);
        await repository.Save(store.Id, route, revision, cancellationToken);
        checkoutPolicy?.RememberRoute(route);
        return route;
    }

    private long CheckoutExpiry(long requestedExpiry, long quoteExpiry)
    {
        if (_timingOptions.OutgoingFundingSafetySeconds < 0 || _timingOptions.MinimumCheckoutWindowSeconds < 1)
            throw new InvalidOperationException("The composed quote timing policy is invalid.");
        var safeQuoteExpiry = checked(quoteExpiry - _timingOptions.OutgoingFundingSafetySeconds);
        var checkoutExpiry = Math.Min(requestedExpiry, safeQuoteExpiry);
        if (checkoutExpiry - (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds() <
            _timingOptions.MinimumCheckoutWindowSeconds)
            throw new InvalidOperationException("The composed quote leaves too little checkout time.");
        return checkoutExpiry;
    }

    private static async Task<T> InvokeAdapter<T>(Func<Task<T>> invoke, CancellationToken cancellationToken)
    {
        try { return await invoke(); }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The composed quote adapter is unavailable; inspect the public route journal before retrying.");
        }
    }

    internal static ArkadePaymentMethodConfig? Configuration(StoreData store) =>
        store.GetPaymentMethodConfigs(true).TryGetValue(ArkadePlugin.ArkadePaymentMethodId, out var value)
            ? value.ToObject<ArkadePaymentMethodConfig>(BlobSerializer.CreateSerializer().Serializer)
            : null;
}

public sealed class ArkCompositionUnavailableException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
