using System.Text.Json;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using NArk.Abstractions.Blockchain;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;
using NArk.Core.Transport;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed record ArkCompositionSourceOutput(string TransactionId, uint OutputIndex, long AmountSats);
public sealed record ArkCompositionSourceFunding(string StoreId, Guid RouteId, string PaymentHash,
    string Destination, ArkCompositionSourceOutput[] Outputs);

public sealed class ArkCompositionSourceEvidence(ISettingsRepository settings, IBitcoinBlockchain blockchain,
    IClientTransport transport, ArkCompositionPrivateStore privateStore, ArkCompositionSolverFactory solvers)
{
    private const int MaxEvidenceOutputs = 100;
    private const int MaxScanOutputs = 16_384;
    private const int MaxSearchCandidates = 4_096;
    private const int MaxSubsetStates = 10_000;
    private const int MaxSubsetTransitions = 250_000;

    public async Task CaptureAsync(ArkInvoiceComposition route, CancellationToken cancellationToken)
    {
        if (route.PaymentMethodId != "BTC-CHAIN" || await ReadAsync(route) is not null) return;
        var outputs = await blockchain.GetUtxosAsync(route.CustomerDestination!, cancellationToken);
        var expected = checked(route.BaseAmountSats!.Value + route.IngressFeeSats);
        var selected = SelectExact(outputs, expected);
        if (selected.Length > 0)
        {
            await CaptureKnownAsync(route, selected.Select(u => new OutPoint(uint256.Parse(u.Txid), u.Vout)).ToArray(), cancellationToken);
            return;
        }
        await RecoverAsync(route, expected, cancellationToken);
    }

    public async Task<ArkCompositionSourceFunding?> ReadOrCaptureAsync(ArkInvoiceComposition route,
        CancellationToken cancellationToken)
    {
        await CaptureAsync(route, cancellationToken);
        return await ReadAsync(route);
    }

    public async Task CaptureKnownAsync(ArkInvoiceComposition route, IReadOnlyList<OutPoint> outpoints,
        CancellationToken cancellationToken = default)
    {
        if (route.PaymentMethodId != "BTC-CHAIN" || route.PaymentHash is null || route.CustomerDestination is null
            || outpoints.Count == 0 || outpoints.Count > MaxEvidenceOutputs
            || outpoints.Distinct().Count() != outpoints.Count)
            throw new InvalidOperationException("Exact onchain source funding evidence is required.");
        var server = await transport.GetServerInfoAsync(cancellationToken);
        var script = BitcoinAddress.Create(route.CustomerDestination, server.Network).ScriptPubKey;
        var verified = new List<ArkCompositionSourceOutput>();
        foreach (var point in outpoints)
        {
            var transaction = await blockchain.GetRawTransactionAsync(point.Hash, cancellationToken);
            if (transaction is null || transaction.GetHash() != point.Hash || point.N >= transaction.Outputs.Count
                || transaction.Outputs[point.N].ScriptPubKey != script || transaction.Outputs[point.N].Value.Satoshi <= 0)
                throw new InvalidOperationException("Onchain funding evidence does not pay the route's customer destination.");
            verified.Add(new ArkCompositionSourceOutput(point.Hash.ToString(), point.N, transaction.Outputs[point.N].Value.Satoshi));
        }
        if (verified.Sum(v => v.AmountSats) != checked(route.BaseAmountSats!.Value + route.IngressFeeSats))
            throw new InvalidOperationException("Onchain funding evidence does not have the route's exact amount.");
        var funding = new ArkCompositionSourceFunding(route.StoreId, route.RouteId, route.PaymentHash,
            route.CustomerDestination, verified.OrderBy(v => v.TransactionId).ThenBy(v => v.OutputIndex).ToArray());
        var existing = await ReadAsync(route);
        if (existing is not null)
        {
            if (!existing.Outputs.SequenceEqual(funding.Outputs))
                throw new InvalidOperationException("Onchain source funding evidence is immutable.");
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await settings.UpdateSetting(funding, Name(route));
    }

    public async Task<ArkCompositionSourceFunding?> ReadAsync(ArkInvoiceComposition route)
    {
        var result = await settings.GetSettingAsync<ArkCompositionSourceFunding>(Name(route));
        if (result is not null && (result.StoreId != route.StoreId || result.RouteId != route.RouteId
            || result.PaymentHash != route.PaymentHash || result.Destination != route.CustomerDestination
            || result.Outputs.Length == 0 || result.Outputs.Any(o => o.AmountSats <= 0)
            || result.Outputs.Sum(o => o.AmountSats) != checked(route.BaseAmountSats!.Value + route.IngressFeeSats)))
            throw new InvalidOperationException("Onchain source evidence differs from its route.");
        return result;
    }

    private static string Name(ArkInvoiceComposition route) => $"ArkCompositionSourceFunding-{route.RouteId:N}";

    private async Task RecoverAsync(ArkInvoiceComposition route, long expected, CancellationToken cancellationToken)
    {
        var recovery = await privateStore.ReadAsync<RecoveryProjection>(route.StoreId, route.RouteId);
        if (recovery?.Request is null || recovery.IngressSolver is null) return;
        var request = JsonSerializer.Deserialize<ArkCompositionExecutionRequest>(recovery.Request);
        if (!Matches(route, request)) return;
        var ingress = route.Legs.SingleOrDefault(leg => leg.Kind == "Ingress");
        if (ingress?.SolverPubkey is null || ingress.LockupAddress is null) return;
        ArkEvmSolverSelection selection;
        try { selection = recovery.IngressSolver.Validate(); }
        catch (ArgumentException) { return; }
        if (selection.Mode != "explicit"
            || selection.SolverPubkey is not null && selection.SolverPubkey != ingress.SolverPubkey) return;
        var server = await transport.GetServerInfoAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(server.NetworkName)) return;
        using var solver = await solvers.OpenAsync(selection, server.NetworkName,
            request.AssetId, request.SourceRail, request.BaseAmountSats, cancellationToken);
        solver.VerifyIdentity(ingress.SolverPubkey);
        var status = await solver.Transport.GetStatusAsync<OnchainReceiveStatusProfile>(ingress.RfqId, cancellationToken);
        if (status?.RfqId != ingress.RfqId || status.Profile?.PaymentHash != route.PaymentHash
            || status.Profile.HtlcAddress != route.CustomerDestination
            || status.Profile.LockupAddress != ingress.LockupAddress
            || !uint256.TryParse(status.Profile.FundingTxid, out var hintedTxid)) return;
        var transaction = await blockchain.GetRawTransactionAsync(hintedTxid, cancellationToken);
        if (transaction is null || transaction.GetHash() != hintedTxid
            || transaction.Outputs.Count > MaxScanOutputs) return;
        var script = BitcoinAddress.Create(route.CustomerDestination!, server.Network).ScriptPubKey;
        var candidates = transaction.Outputs.Select((output, index) => (output, index))
            .Where(item => item.output.ScriptPubKey == script && item.output.Value.Satoshi > 0)
            .Select(item => new BoardingUtxo(hintedTxid.ToString(), checked((uint)item.index),
                checked((ulong)item.output.Value.Satoshi), true, 0, 0))
            .ToArray();
        var selected = SelectExact(candidates, expected);
        if (selected.Length == 0) return;
        await CaptureKnownAsync(route, selected.Select(output =>
            new OutPoint(hintedTxid, output.Vout)).ToArray(), cancellationToken);
    }

    private static bool Matches(ArkInvoiceComposition route, ArkCompositionExecutionRequest? request) =>
        request is not null && request.StoreId == route.StoreId && request.RouteId == route.RouteId
        && request.WalletId == route.WalletId && request.SourceRail == route.PaymentMethodId
        && request.SourceRail == "BTC-CHAIN" && request.BaseAmountSats == route.BaseAmountSats
        && request.AssetId == route.AssetId && request.Destination == route.Destination
        && request.Policy?.SwapContractAddress == route.SwapContractAddress;

    private static BoardingUtxo[] SelectExact(IReadOnlyList<BoardingUtxo> outputs, long expected)
    {
        if (expected <= 0 || outputs.Count > MaxScanOutputs) return [];
        var candidatesByOutpoint = new Dictionary<(string Txid, uint Vout), BoardingUtxo>();
        BoardingUtxo? single = null;
        var exceededCandidateLimit = false;
        foreach (var output in outputs)
        {
            if (output.Amount == (ulong)expected && (single is null
                || StringComparer.Ordinal.Compare(output.Txid, single.Txid) < 0
                || output.Txid == single.Txid && output.Vout < single.Vout))
                single = output;
            if (output.Amount == 0 || output.Amount > (ulong)expected) continue;
            var key = (output.Txid, output.Vout);
            if (candidatesByOutpoint.TryGetValue(key, out var duplicate))
            {
                if (duplicate.Amount != output.Amount) return [];
                continue;
            }
            if (candidatesByOutpoint.Count == MaxSearchCandidates)
            {
                exceededCandidateLimit = true;
                continue;
            }
            candidatesByOutpoint.Add(key, output);
        }
        if (single is not null) return [single];
        if (exceededCandidateLimit) return [];

        var candidates = candidatesByOutpoint.Values
            .OrderByDescending(output => output.Amount)
            .ThenBy(output => output.Txid, StringComparer.Ordinal)
            .ThenBy(output => output.Vout)
            .ToArray();
        foreach (var transaction in candidates.GroupBy(output => output.Txid, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var grouped = transaction.ToArray();
            if (grouped.Length > MaxEvidenceOutputs) continue;
            long total = 0;
            foreach (var output in grouped)
            {
                if ((long)output.Amount > expected - total)
                {
                    total = -1;
                    break;
                }
                total += (long)output.Amount;
            }
            if (total == expected) return grouped;
        }

        return FindBoundedSubset(candidates, expected);
    }

    // These bounds cap adversarial UTXO scans; exceeding either search budget deliberately fails closed.
    private static BoardingUtxo[] FindBoundedSubset(IReadOnlyList<BoardingUtxo> candidates, long expected)
    {
        var sums = new Dictionary<long, Selection?> { [0] = null };
        var transitions = 0;
        foreach (var candidate in candidates)
        {
            foreach (var partial in sums.ToArray())
            {
                if (++transitions > MaxSubsetTransitions) return [];
                var count = (partial.Value?.Count ?? 0) + 1;
                if (count > MaxEvidenceOutputs || (long)candidate.Amount > expected - partial.Key) continue;
                var total = partial.Key + (long)candidate.Amount;
                if (sums.TryGetValue(total, out var previous) && previous is not null && previous.Count <= count)
                    continue;
                var selected = new Selection(candidate, partial.Value, count);
                if (total == expected) return selected.ToArray();
                if (!sums.ContainsKey(total) && sums.Count >= MaxSubsetStates) return [];
                sums[total] = selected;
            }
        }
        return [];
    }

    private sealed record Selection(BoardingUtxo Output, Selection? Previous, int Count)
    {
        public BoardingUtxo[] ToArray()
        {
            var result = new BoardingUtxo[Count];
            Selection? current = this;
            for (var index = Count - 1; index >= 0; index--)
            {
                result[index] = current!.Output;
                current = current.Previous;
            }
            return result;
        }
    }

    private sealed class RecoveryProjection
    {
        public string? Request { get; init; }
        public ArkEvmSolverSelection? IngressSolver { get; init; }
    }
}
