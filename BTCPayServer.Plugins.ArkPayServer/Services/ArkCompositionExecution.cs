using BTCPayServer.Plugins.ArkPayServer.Data;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed record ArkCompositionFunding(string TransactionId, long AmountSats);
public sealed record ArkCompositionObservation(ArkCompositionFunding? IngressFunding,
    string? IngressClaimTransactionId, ArkCompositionFunding? OutgoingFunding);
public sealed record ArkCompositionExecutionOutcome(ArkCompositionEvmLockProof? LockProof = null,
    string? VerifiedClaimTransactionId = null, string? DeliveredAmount = null, bool Refunded = false);
public sealed record ArkCompositionRouteKey(string StoreId, Guid RouteId);

public interface IArkCompositionExecutionBackend
{
    Task<ArkCompositionObservation> ObserveAsync(ArkInvoiceComposition route, CancellationToken cancellationToken);
    Task<ArkCompositionExecutionOutcome> AdvanceAsync(ArkInvoiceComposition route, CancellationToken cancellationToken);
}

public interface IArkCompositionExecutionJournal
{
    Task<IReadOnlyList<ArkCompositionRouteKey>> ListAsync(int skip, int take, CancellationToken cancellationToken);
    Task<ArkInvoiceComposition?> GetAsync(string storeId, Guid routeId, CancellationToken cancellationToken);
    Task SaveAsync(ArkInvoiceComposition route, long expectedRevision, CancellationToken cancellationToken);
}

public interface IArkCompositionExecutionLock
{
    bool SupportsCrossProcessExecution => false;
    ValueTask<IAsyncDisposable> AcquireAsync(string scope, CancellationToken cancellationToken);
}

public interface IArkCompositionPaymentSink
{
    Task SettleAsync(ArkInvoiceComposition route, CancellationToken cancellationToken);
}

public sealed class ArkCompositionExecutionService(IArkCompositionExecutionJournal journal,
    IArkCompositionExecutionBackend backend, IArkCompositionExecutionLock executionLock,
    IArkCompositionPaymentSink payments)
{
    public async Task AdvanceAsync(string storeId, Guid routeId, CancellationToken cancellationToken = default)
    {
        await using var lease = await executionLock.AcquireAsync($"route:{storeId}:{routeId:N}", cancellationToken);
        var route = await journal.GetAsync(storeId, routeId, cancellationToken);
        if (route is null || route.CustomerDestination is null) return;
        try
        {
            if (!route.SettlementVerified)
            {
                await RecordAsync(route, await backend.ObserveAsync(route, cancellationToken), cancellationToken);
                var result = await backend.AdvanceAsync(route, cancellationToken);
                await RecordAsync(route, await backend.ObserveAsync(route, cancellationToken), cancellationToken);
                var revision = route.Revision;
                if (result.Refunded) route.RecordFailure(ArkCompositionFailure.RefundRequired);
                if (result.LockProof is not null) route.RecordEvmLockProven(result.LockProof);
                if (result.VerifiedClaimTransactionId is not null)
                {
                    if (result.LockProof is null || result.DeliveredAmount is null)
                        throw new InvalidOperationException("The execution result lacks verified EVM delivery evidence.");
                    route.RecordEvmClaimVerified(result.VerifiedClaimTransactionId, result.DeliveredAmount);
                }
                if (route.Revision != revision) await journal.SaveAsync(route, revision, cancellationToken);
            }
            if (route.SettlementVerified && route.InvoiceId is not null)
                await payments.SettleAsync(route, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Reload because a failed save may have left this object ahead of the durable journal.
            var stored = await journal.GetAsync(storeId, routeId, cancellationToken);
            if (stored is not null && !stored.SettlementVerified)
            {
                var revision = stored.Revision;
                stored.RecordFailure(ArkCompositionFailure.RemoteUnavailable);
                if (stored.Revision != revision) await journal.SaveAsync(stored, revision, cancellationToken);
            }
            throw new InvalidOperationException("The composition requires another execution or payment-delivery attempt.");
        }
    }

    private async Task RecordAsync(ArkInvoiceComposition route, ArkCompositionObservation observation,
        CancellationToken cancellationToken)
    {
        var revision = route.Revision;
        if (observation.IngressFunding is { } ingress)
            route.RecordIngressLockupFunded(ingress.TransactionId, ingress.AmountSats);
        if (observation.IngressClaimTransactionId is { } claim)
            route.RecordIngressClaimedToOutgoing(claim);
        if (observation.OutgoingFunding is { } outgoing)
            route.RecordOutgoingLockupFunded(outgoing.TransactionId, outgoing.AmountSats);
        if (revision != route.Revision) await journal.SaveAsync(route, revision, cancellationToken);
    }
}
