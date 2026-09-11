using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>SDK adapter boundary; preparation retains P privately, and quotes return validated public facts only.</summary>
public interface IArkCompositionExecutor
{
    /// <summary>Locally create or load one protected P and RFQ reservations keyed by RouteId; do not quote or fund.</summary>
    Task<ArkCompositionPreparation> PrepareAsync(ArkCompositionExecutionRequest request, CancellationToken cancellationToken);
    /// <summary>Validate the outgoing quote for L and its exact destination tuple without revealing P or funding.</summary>
    Task<ArkCompositionOutgoingQuote> QuoteOutgoingAsync(ArkCompositionExecutionRequest request,
        ArkCompositionPreparation prepared, CancellationToken cancellationToken);
    /// <summary>Validate ingress with the same H, payout L, exact amounts and customer destination; do not claim or reveal P.</summary>
    Task<ArkCompositionIngressQuote> QuoteIngressAsync(ArkCompositionExecutionRequest request,
        ArkCompositionPreparation prepared, ArkCompositionQuote outgoing, CancellationToken cancellationToken);
}

public sealed record ArkCompositionExecutionRequest(Guid RouteId, string WalletId, string SourceRail,
    long BaseAmountSats, string AssetId, string Destination, ArkEvmRoutePolicy Policy)
{
    public string StoreId { get; init; } = "";
    public override string ToString() => nameof(ArkCompositionExecutionRequest);
}

public sealed record ArkCompositionPreparation(string PaymentHash, string OutgoingRfqId, string? IngressRfqId);
public sealed record ArkCompositionOutgoingQuote(ArkCompositionQuote Quote, ArkCompositionEvmTerms Terms);
public sealed record ArkCompositionIngressQuote(ArkCompositionQuote Quote, string CustomerDestination, long ExpiresAt);
