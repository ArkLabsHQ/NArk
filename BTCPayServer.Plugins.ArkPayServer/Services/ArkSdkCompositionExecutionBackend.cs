using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BTCPayServer.Plugins.ArkPayServer.Data;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Recovery;
using NArk.ArkadeIntents.Services;
using NArk.Core.Transport;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkSdkCompositionExecutionBackend(IArkadeIntentStorage intents, IVtxoStorage vtxos,
    IBitcoinBlockchain blockchain, IContractStorage contracts, IClientTransport transport, LightningIntentsClient lightning,
    OnchainIntentsClient onchain, ArkCompositionPrivateStore privateStore,
    ArkCompositionEvmContextFactory contexts, IArkCompositionExecutionLock executionLock,
    ArkCompositionSourceEvidence sourceEvidence, TimeProvider? timeProvider = null) : IArkCompositionExecutionBackend
{
    public async Task<ArkCompositionObservation> ObserveAsync(ArkInvoiceComposition route, CancellationToken cancellationToken)
    {
        var (outgoing, ingress) = await LoadAsync(route, cancellationToken);
        await ReconcileAsync(outgoing, cancellationToken);
        if (ingress is not null) await ReconcileAsync(ingress, cancellationToken);
        (outgoing, ingress) = await LoadAsync(route, cancellationToken);
        var incoming = ingress is null ? null : await FundingAsync(route, ingress, cancellationToken);
        var outgoingFunding = await FundingAsync(route, outgoing, cancellationToken);
        var claimed = ingress?.Status == ArkadeSwapIntentStatus.Fulfilled ? ingress.SpentTxid : null;
        if (outgoingFunding is not null && ingress is not null && claimed != outgoingFunding.TransactionId)
            throw new InvalidOperationException("The outgoing funding does not match the observed ingress claim.");
        return new ArkCompositionObservation(incoming, claimed, outgoingFunding);
    }

    public async Task<ArkCompositionExecutionOutcome> AdvanceAsync(ArkInvoiceComposition route, CancellationToken cancellationToken)
    {
        var (outgoing, ingress) = await LoadAsync(route, cancellationToken);
        await ObserveAsync(route, cancellationToken);
        (outgoing, ingress) = await LoadAsync(route, cancellationToken);
        if (outgoing.Status == ArkadeSwapIntentStatus.Cancelled)
            return new ArkCompositionExecutionOutcome(Refunded: true);
        await sourceEvidence.CaptureAsync(route, cancellationToken);
        var recovery = await privateStore.ReadAsync<PreparedRequest>(route.StoreId, route.RouteId)
            ?? throw new InvalidOperationException("Protected composition execution context is unavailable.");
        var request = JsonSerializer.Deserialize<ArkCompositionExecutionRequest>(recovery.Request)
            ?? throw new InvalidOperationException("Protected composition execution context is invalid.");
        if (request.StoreId != route.StoreId || request.RouteId != route.RouteId || request.WalletId != route.WalletId
            || request.SourceRail != route.PaymentMethodId || request.BaseAmountSats != route.BaseAmountSats
            || request.AssetId != route.AssetId || request.Destination != route.Destination
            || request.Policy.SwapContractAddress != route.SwapContractAddress)
            throw new InvalidOperationException("Protected execution context differs from the public route.");
        using var context = await contexts.OpenForRecoveryAsync(request, cancellationToken);
        await using var senderLease = await executionLock.AcquireAsync(
            $"sender:{context.Policy.ChainId}:{context.Sender.Address.ToLowerInvariant()}", cancellationToken);
        var client = new ComposedSwapExecutionClient(intents, vtxos, blockchain, contracts, transport, lightning, onchain,
            context.Rpc, context.Sender, context.Policy, timeProvider);
        var result = await client.AdvanceAsync(outgoing.Id, ingress?.Id, cancellationToken);
        if (result.OutgoingSwapId != outgoing.Id || result.IngressSwapId != ingress?.Id)
            throw new InvalidOperationException("The SDK execution result identifies a different route.");
        var proof = result.LockProof is null ? null : new ArkCompositionEvmLockProof(null,
            result.LockProof.ObservedAtBlock.ToString(CultureInfo.InvariantCulture),
            result.LockProof.ProvenAtBlock.ToString(CultureInfo.InvariantCulture), result.LockProof.ProvenBlockTimestamp);
        return new ArkCompositionExecutionOutcome(proof,
            result.OutgoingStatus == ArkadeSwapIntentStatus.Fulfilled ? result.EvmClaimTxid : null,
            result.OutgoingStatus == ArkadeSwapIntentStatus.Fulfilled ? result.DeliveredAmount : null,
            result.OutgoingStatus == ArkadeSwapIntentStatus.Cancelled);
    }

    private async Task<(ArkadeSwapIntent Outgoing, ArkadeSwapIntent? Ingress)> LoadAsync(
        ArkInvoiceComposition route, CancellationToken cancellationToken)
    {
        var outgoingLeg = route.Legs.Single(l => l.Kind == "Outgoing");
        var outgoing = await intents.GetArkadeSwapIntent(outgoingLeg.RfqId, cancellationToken)
            ?? throw new InvalidOperationException("The outgoing SDK intent is unavailable.");
        ValidateIntent(route, outgoingLeg, outgoing);
        if (outgoing.Type != ArkadeSwapIntentType.BtcToEvm || outgoing.ToAssetId != route.AssetId
            || outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmAmount) != route.EvmAmount
            || outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmTokenAddress) != route.EvmTokenAddress
            || outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimAddress) != route.EvmClaimAddress
            || outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmRefundAddress) != route.EvmRefundAddress
            || outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmTimeoutBlock) != route.EvmTimeoutBlock
            || outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmSwapContractAddress) != route.SwapContractAddress)
            throw new InvalidOperationException("The outgoing SDK terms differ from the public route.");
        var ingressLeg = route.Legs.SingleOrDefault(l => l.Kind == "Ingress");
        if (ingressLeg is null)
        {
            if (route.PaymentMethodId != "ARKADE") throw new InvalidOperationException("The route requires an ingress intent.");
            return (outgoing, null);
        }
        var ingress = await intents.GetArkadeSwapIntent(ingressLeg.RfqId, cancellationToken)
            ?? throw new InvalidOperationException("The ingress SDK intent is unavailable.");
        ValidateIntent(route, ingressLeg, ingress);
        if (ingress.Type != (route.PaymentMethodId == "BTC-LN" ? ArkadeSwapIntentType.LightningToBtc : ArkadeSwapIntentType.OnchainToBtc)
            || ingress.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.ComposedOutgoingSwapId) != outgoing.Id
            || ingress.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.ComposedPayoutPkScript) != outgoing.SwapPkScript)
            throw new InvalidOperationException("The ingress SDK linkage differs from the public route.");
        return (outgoing, ingress);
    }

    private static void ValidateIntent(ArkInvoiceComposition route, ArkInvoiceCompositionLeg leg, ArkadeSwapIntent intent)
    {
        if (intent.Id != leg.RfqId || intent.WalletId != route.WalletId || intent.PaymentHash != route.PaymentHash
            || intent.SwapPkScript != leg.LockupScript || intent.SwapAddress != leg.LockupAddress
            || intent.SolverPubkey() != leg.SolverPubkey
            || intent.OfferAmount.Satoshi.ToString(CultureInfo.InvariantCulture) != leg.FromAmount
            || leg.Kind == "Ingress" && intent.WantAmount.Satoshi.ToString(CultureInfo.InvariantCulture) != leg.ToAmount
            || intent.RefundLocktime != leg.RefundLocktime)
            throw new InvalidOperationException("An SDK intent differs from its public route journal.");
    }

    private async Task ReconcileAsync(ArkadeSwapIntent intent, CancellationToken cancellationToken)
    {
        if (ArkadeSwapStateMachine.Terminal.Contains(intent.Status) && intent.Status != ArkadeSwapIntentStatus.Resolved) return;
        var outputs = await vtxos.GetVtxos(scripts: [intent.SwapPkScript], includeSpent: true,
            cancellationToken: cancellationToken);
        var output = outputs.FirstOrDefault(v => !v.IsSpent() && !v.Swept) ?? outputs.FirstOrDefault();
        if (output is null) return;
        var spender = output.SpentByTransactionId ?? output.SettledByTransactionId;
        var revealed = output.IsSpent() && spender is not null
            ? await SwapPreimageReader.FindAsync(transport, output.OutPoint, spender, intent.PaymentHash!, cancellationToken) : null;
        var provesClaim = revealed is not null;
        if (revealed is not null) CryptographicOperations.ZeroMemory(revealed);
        var claimedStatus = intent.Type == ArkadeSwapIntentType.BtcToEvm
            ? ArkadeSwapIntentStatus.Claimable : ArkadeSwapIntentStatus.Fulfilled;
        var refunded = false;
        if (intent.Status == ArkadeSwapIntentStatus.Cancelling && output.IsSpent() && !provesClaim)
        {
            var fate = await LockupFateReader.ReadAsync(transport, vtxos, intent.SwapPkScript, intent.PaymentHash!, cancellationToken);
            refunded = fate.Fate == LockupFate.Returned;
            if (fate.Preimage is not null) CryptographicOperations.ZeroMemory(fate.Preimage);
        }
        ArkadeSwapIntentStatus? next = intent.Status switch
        {
            ArkadeSwapIntentStatus.Resolved => provesClaim ? claimedStatus : null,
            ArkadeSwapIntentStatus.Cancelling when !output.IsSpent() => ArkadeSwapIntentStatus.Pending,
            ArkadeSwapIntentStatus.Cancelling when provesClaim => claimedStatus,
            ArkadeSwapIntentStatus.Cancelling when refunded => ArkadeSwapIntentStatus.Cancelled,
            _ => ArkadeSwapStateMachine.Next(intent.Type, intent.Status,
                SwapObservation.From(output, (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds(),
                    intent.RefundLocktime, provesClaim))
        };
        if (next is null || next == intent.Status) return;
        intent.Status = next.Value;
        if (output.IsSpent()) intent.SpentTxid ??= output.ArkTxid ?? spender;
        await intents.SaveArkadeSwapIntent(intent, cancellationToken);
    }

    private async Task<ArkCompositionFunding?> FundingAsync(ArkInvoiceComposition route, ArkadeSwapIntent intent,
        CancellationToken cancellationToken)
    {
        var outputs = await vtxos.GetVtxos(scripts: [intent.SwapPkScript], includeSpent: true,
            cancellationToken: cancellationToken);
        if (outputs.Count == 0) return null;
        if (outputs.Any(v => v.Script != intent.SwapPkScript || v.Assets is { Count: > 0 }))
            throw new InvalidOperationException("The observed covenant funding carries unexpected terms.");
        var total = outputs.Aggregate(0L, (sum, v) => checked(sum + checked((long)v.Amount)));
        var transactions = outputs.Select(v => v.TransactionId).Distinct().ToArray();
        if (total != route.BaseAmountSats || transactions.Length != 1)
            throw new InvalidOperationException("The observed covenant funding is not the exact quoted amount.");
        return new ArkCompositionFunding(transactions[0], total);
    }

    private sealed class PreparedRequest
    {
        public string Request { get; set; } = "";
        public override string ToString() => nameof(PreparedRequest);
    }
}
