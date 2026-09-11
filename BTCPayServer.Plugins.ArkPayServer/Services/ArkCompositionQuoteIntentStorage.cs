using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkCompositionQuoteIntentStorage(IArkadeIntentStorage inner) : IArkadeIntentStorage
{
    public event EventHandler<ArkadeSwapIntent>? SwapsChanged { add => inner.SwapsChanged += value; remove => inner.SwapsChanged -= value; }
    public event EventHandler? ActiveScriptsChanged { add => inner.ActiveScriptsChanged += value; remove => inner.ActiveScriptsChanged -= value; }
    public Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(string? id = null,
        ArkadeSwapIntentStatus? status = null, ArkadeSwapIntentStatus[]? statuses = null, string? swapPkScript = null,
        string[]? walletIds = null, int? skip = null, int? take = null, CancellationToken cancellationToken = default) =>
        inner.GetArkadeSwapIntents(id, status, statuses, swapPkScript, walletIds, skip, take, cancellationToken);

    public async Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default)
    {
        var existing = await inner.GetArkadeSwapIntent(intent.Id, cancellationToken);
        if (existing is null) { await inner.SaveArkadeSwapIntent(intent, cancellationToken); return; }
        if (intent.Status != ArkadeSwapIntentStatus.Pending || existing.WalletId != intent.WalletId
            || existing.Type != intent.Type || existing.PaymentHash != intent.PaymentHash
            || existing.FromAssetId != intent.FromAssetId || existing.ToAssetId != intent.ToAssetId
            || existing.OfferAmount != intent.OfferAmount || existing.WantAmount != intent.WantAmount
            || existing.SwapPkScript != intent.SwapPkScript || existing.SwapAddress != intent.SwapAddress
            || existing.RefundLocktime != intent.RefundLocktime
            || intent.Metadata.Any(pair => existing.Metadata.GetValueOrDefault(pair.Key) != pair.Value))
            throw new InvalidOperationException("The replayed quote differs from its persisted SDK intent.");
    }

    public Task<bool> UpdateStatus(string swapPkScript, ArkadeSwapIntentStatus status, string? spentTxid = null,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("A quote cannot advance a swap.");
}
