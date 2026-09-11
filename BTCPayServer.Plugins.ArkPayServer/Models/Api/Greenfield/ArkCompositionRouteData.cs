using BTCPayServer.Plugins.ArkPayServer.Data;

namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>Explicit public route projection, excluding wallet capabilities and SDK secrets.</summary>
/// <param name="RouteId">Independent prompt or renewal identity.</param>
/// <param name="StoreId">Owning store.</param>
/// <param name="InvoiceId">Exact attached invoice, or null before prompt persistence.</param>
/// <param name="PaymentMethodId">Source rail.</param>
/// <param name="PaymentHash">Public route hash H, absent before SDK preparation.</param>
/// <param name="AssetId">CAIP-19 destination asset.</param>
/// <param name="Destination">Merchant EVM address.</param>
/// <param name="CreatedAt">UTC creation time.</param>
/// <param name="Revision">Optimistic concurrency revision.</param>
/// <param name="Status">Observed-money state.</param>
/// <param name="BaseAmountSats">Exact BTC amount required by L.</param>
/// <param name="IngressFeeSats">Customer ingress spread.</param>
/// <param name="FailureCode">Closed nonsecret recovery code.</param>
/// <param name="Legs">Prepared RFQs and accepted public quotes.</param>
/// <param name="EvmTerms">Exact six-value tuple and allowed contract.</param>
/// <param name="IngressClaimTransactionId">M-to-L transaction.</param>
/// <param name="EvmLockProof">SDK-verified public proof coordinates.</param>
/// <param name="EvmClaimTransactionId">Transaction proving token delivery.</param>
public sealed record ArkCompositionRouteData(Guid RouteId, string StoreId, string? InvoiceId, string PaymentMethodId,
    string? PaymentHash, string AssetId, string Destination, DateTimeOffset CreatedAt, long Revision, string Status,
    long? BaseAmountSats, long IngressFeeSats, string? FailureCode, ArkCompositionLegData[] Legs,
    ArkCompositionEvmTerms? EvmTerms, string? IngressClaimTransactionId, ArkCompositionEvmLockProof? EvmLockProof,
    string? EvmClaimTransactionId, string? CustomerDestination, long? CheckoutExpiresAt, bool ExecutionAvailable)
{
    /// <summary>Ingress payment never constitutes completion.</summary>
    public bool SettlementVerified => Status == "EvmClaimVerified";
    /// <summary>Fixed merchant-delivery completion policy.</summary>
    public string PaymentCompletionCondition => "evm-settlement";

    /// <summary>Projects only explicitly allowlisted public lifecycle fields.</summary>
    public static ArkCompositionRouteData From(ArkInvoiceComposition route, bool executionAvailable) => new(route.RouteId, route.StoreId,
        route.InvoiceId, route.PaymentMethodId, route.PaymentHash, route.AssetId, route.Destination, route.CreatedAt,
        route.Revision, route.Status, route.BaseAmountSats, route.IngressFeeSats, route.FailureCode?.ToString(),
        route.Legs.OrderBy(l => l.Kind).Select(l => new ArkCompositionLegData(l.RfqId, l.Kind,
            l.Quote(route.PaymentHash!), l.FundingTransactionId, l.FundedAmountSats)).ToArray(),
        route.EvmAmount is null ? null : new ArkCompositionEvmTerms(route.PaymentHash!, route.EvmAmount,
            route.EvmTokenAddress!, route.EvmClaimAddress!, route.EvmRefundAddress!, route.EvmTimeoutBlock!, route.SwapContractAddress!),
        route.IngressClaimTransactionId, route.EvmObservedAtBlock is null ? null : new ArkCompositionEvmLockProof(
            route.EvmLockTransactionId, route.EvmObservedAtBlock, route.EvmProvenAtBlock!, route.EvmProvenBlockTimestamp!.Value),
        route.EvmClaimTransactionId, route.CustomerDestination, route.CheckoutExpiresAt, executionAvailable);
}

/// <summary>Public prepared RFQ, optional quote and observed Arkade funding.</summary>
/// <param name="RfqId">Globally reserved correlation id.</param>
/// <param name="Kind">Outgoing or Ingress.</param>
/// <param name="Quote">Accepted public quote, absent while prepared.</param>
/// <param name="FundingTransactionId">Transaction proving L or M funding.</param>
/// <param name="FundedAmountSats">Observed Arkade amount.</param>
public sealed record ArkCompositionLegData(string RfqId, string Kind, ArkCompositionQuote? Quote,
    string? FundingTransactionId, long? FundedAmountSats);
