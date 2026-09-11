using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.SolverRegistry;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkSdkCompositionExecutor(ArkCompositionPrivateStore privateStore,
    ArkCompositionEvmContextFactory evmContexts, ArkCompositionSolverFactory solvers,
    IClientTransport ark, IContractService contracts, IArkadeIntentStorage intents,
    IServiceProvider services, ArkadeSolverService claimRecipients,
    IOptions<ArkadeIntentsOptions> options) : IArkCompositionExecutor
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ArkCompositionPreparation> PrepareAsync(ArkCompositionExecutionRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var context = await evmContexts.OpenAsync(request, cancellationToken);
            var state = await privateStore.ReadAsync<RecoveryState>(request.StoreId, request.RouteId);
            if (state is not null) { ValidateRequest(state, request); return state.Preparation; }
            var secret = SwapLinkSecret.Generate();
            var bytes = secret.ExportPreimage();
            try
            {
                state = new RecoveryState
                {
                    Request = JsonSerializer.Serialize(request), Preimage = Convert.ToHexString(bytes).ToLowerInvariant(),
                    Preparation = new ArkCompositionPreparation(secret.PaymentHash, RfqProtocol.NewRfqId(),
                        request.SourceRail == "ARKADE" ? null : RfqProtocol.NewRfqId())
                };
                await SaveAsync(request, state, cancellationToken);
                return state.Preparation;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { _gate.Release(); }
    }

    public async Task<ArkCompositionOutgoingQuote> QuoteOutgoingAsync(ArkCompositionExecutionRequest request,
        ArkCompositionPreparation prepared, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadAsync(request, prepared);
            using var context = await evmContexts.OpenAsync(request, cancellationToken);
            if (state.Outgoing is not null) return state.Outgoing;
            var server = await ark.GetServerInfoAsync(cancellationToken);
            var networkName = server.NetworkName ?? throw new InvalidOperationException("The Ark server did not report a network.");
            var refund = state.RefundContract is null
                ? await contracts.DeriveContract(request.WalletId, NextContractPurpose.Receive, cancellationToken: cancellationToken)
                : ArkContractParser.Parse(state.RefundContract, server.Network)
                    ?? throw new InvalidOperationException("The prepared refund contract cannot be recovered.");
            state.RefundContract = refund.ToString();
            using var solver = await solvers.OpenAsync(state.OutgoingSolver ?? request.Policy.OutgoingSolver!, networkName,
                request.AssetId, "ARKADE", request.BaseAmountSats, cancellationToken);
            state.OutgoingSolver ??= solver.Selection;
            state.OutgoingCard ??= solver.Card;
            await SaveAsync(request, state, cancellationToken);
            var transport = new ArkCompositionRfqCheckpoint(solver.Transport, state.OutgoingRfq,
                token => SaveAsync(request, state, token));
            var client = new EvmIntentsClient(ark, contracts, new ArkCompositionQuoteIntentStorage(intents), context.Rpc, options);
            var pending = await client.CreateSendQuoteAsync(request.WalletId, request.BaseAmountSats,
                request.Destination, context.Policy, transport, Secret(state), prepared.OutgoingRfqId,
                state.OutgoingCard, refund, cancellationToken);
            solver.VerifyIdentity(pending.Quote.SolverPubkey);
            var values = pending.EvmValues;
            state.OutgoingContract = pending.Contract.ToString();
            state.Outgoing = new ArkCompositionOutgoingQuote(
                Facts(pending.Quote, prepared.PaymentHash, pending.Contract, pending.LockupAddress),
                new ArkCompositionEvmTerms(values.PaymentHash, Number(values.Amount), values.TokenAddress,
                    values.ClaimAddress, values.RefundAddress, Number(values.TimeoutBlock), context.Policy.SwapContractAddress));
            ValidateOutgoing(request, prepared, state.Outgoing);
            await SaveAsync(request, state, cancellationToken);
            return state.Outgoing;
        }
        finally { _gate.Release(); }
    }

    public async Task<ArkCompositionIngressQuote> QuoteIngressAsync(ArkCompositionExecutionRequest request,
        ArkCompositionPreparation prepared, ArkCompositionQuote outgoing, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadAsync(request, prepared);
            using var context = await evmContexts.OpenAsync(request, cancellationToken);
            if (request.SourceRail == "ARKADE" || prepared.IngressRfqId is null || state.Outgoing?.Quote != outgoing)
                throw new InvalidOperationException("Ingress requires the persisted independent outgoing quote.");
            if (state.Ingress is not null) return state.Ingress;
            var server = await ark.GetServerInfoAsync(cancellationToken);
            var networkName = server.NetworkName ?? throw new InvalidOperationException("The Ark server did not report a network.");
            var refund = ArkContractParser.Parse(state.RefundContract!, server.Network)
                ?? throw new InvalidOperationException("The outgoing refund contract cannot be recovered.");
            var lockup = ArkContractParser.Parse(state.OutgoingContract!, server.Network) as VHTLCv2Contract
                ?? throw new InvalidOperationException("The outgoing lock cannot be recovered.");
            if (lockup.GetScriptPubKey().ToHex() != outgoing.LockupScript)
                throw new InvalidOperationException("The recovered outgoing lock differs from the journal.");
            var selection = request.SourceRail == "BTC-LN" ? request.Policy.LightningIngressSolver! : request.Policy.OnchainIngressSolver!;
            using var solver = await solvers.OpenAsync(state.IngressSolver ?? selection, networkName, request.AssetId,
                request.SourceRail, request.BaseAmountSats, cancellationToken);
            state.IngressSolver ??= solver.Selection;
            state.IngressCard ??= solver.Card;
            var recipient = await claimRecipients.ResolveClaimRecipientAsync(cancellationToken);
            BitcoinAddress? refundAddress = null;
            if (request.SourceRail == "BTC-CHAIN")
            {
                if (state.OnchainRefundAddress is null)
                {
                    var boarding = await contracts.DeriveContract(request.WalletId, NextContractPurpose.Boarding,
                        cancellationToken: cancellationToken) as ArkBoardingContract
                        ?? throw new InvalidOperationException("A tracked onchain refund destination is required.");
                    state.OnchainRefundAddress = boarding.GetOnchainAddress(server.Network).ToString();
                }
                refundAddress = BitcoinAddress.Create(state.OnchainRefundAddress, server.Network);
            }
            await SaveAsync(request, state, cancellationToken);
            var transport = new ArkCompositionRfqCheckpoint(solver.Transport, state.IngressRfq,
                token => SaveAsync(request, state, token));
            var quoteStorage = new ArkCompositionQuoteIntentStorage(intents);
            if (request.SourceRail == "BTC-LN")
            {
                var lightning = ActivatorUtilities.CreateInstance<LightningIntentsClient>(services, quoteStorage);
                var pending = await lightning.ReceiveFromLightningIntoAsync(request.WalletId, request.BaseAmountSats,
                    transport, recipient, Secret(state), lockup.GetArkAddress(), refund, prepared.OutgoingRfqId,
                    state.IngressCard, prepared.IngressRfqId, cancellationToken);
                try
                {
                    solver.VerifyIdentity(pending.Quote.SolverPubkey);
                    state.Ingress = new ArkCompositionIngressQuote(Facts(pending.Quote, pending.PaymentHash,
                        pending.Contract, pending.LockupAddress, PayoutScript(pending.Contract)), pending.Invoice, pending.Quote.ValidUntil);
                }
                finally { CryptographicOperations.ZeroMemory(pending.Preimage); }
            }
            else
            {
                var onchain = ActivatorUtilities.CreateInstance<OnchainIntentsClient>(services, quoteStorage);
                var pending = await onchain.ReceiveFromOnchainIntoAsync(request.WalletId, request.BaseAmountSats,
                    transport, recipient, refundAddress!, Secret(state), lockup.GetArkAddress(), refund,
                    prepared.OutgoingRfqId, state.IngressCard, prepared.IngressRfqId, cancellationToken);
                try
                {
                    solver.VerifyIdentity(pending.Quote.SolverPubkey);
                    state.Ingress = new ArkCompositionIngressQuote(Facts(pending.Quote, pending.PaymentHash,
                        pending.Contract, pending.LockupAddress, PayoutScript(pending.Contract)), pending.HtlcAddress,
                        Math.Min(pending.Quote.ValidUntil, pending.HtlcLocktime));
                }
                finally { CryptographicOperations.ZeroMemory(pending.Preimage); }
            }
            ValidateIngress(request, prepared, outgoing, state.Ingress.Quote);
            await SaveAsync(request, state, cancellationToken);
            return state.Ingress;
        }
        finally { _gate.Release(); }
    }

    public static void ValidateOutgoing(ArkCompositionExecutionRequest request, ArkCompositionPreparation prepared,
        ArkCompositionOutgoingQuote outgoing)
    {
        if (outgoing.Quote.RfqId != prepared.OutgoingRfqId || outgoing.Quote.PaymentHash != prepared.PaymentHash
            || outgoing.Quote.FromAmount != Number(request.BaseAmountSats)
            || outgoing.Terms.PaymentHash != prepared.PaymentHash || outgoing.Terms.ClaimAddress != request.Destination
            || request.AssetId != $"eip155:{ArkCompositionEvmContextFactory.Policy(request.AssetId, request.Policy).ChainId}/erc20:{outgoing.Terms.TokenAddress}"
            || outgoing.Terms.SwapContractAddress != request.Policy.SwapContractAddress)
            throw new InvalidOperationException("The outgoing quote differs from the prepared route.");
    }

    public static void ValidateIngress(ArkCompositionExecutionRequest request, ArkCompositionPreparation prepared,
        ArkCompositionQuote outgoing, ArkCompositionQuote ingress)
    {
        if (ingress.RfqId != prepared.IngressRfqId || ingress.RfqId == outgoing.RfqId
            || ingress.PaymentHash != prepared.PaymentHash || outgoing.PaymentHash != prepared.PaymentHash
            || ingress.ToAmount != Number(request.BaseAmountSats) || outgoing.FromAmount != ingress.ToAmount
            || ingress.PayoutScript != outgoing.LockupScript
            || System.Numerics.BigInteger.Parse(ingress.FromAmount, CultureInfo.InvariantCulture)
                < System.Numerics.BigInteger.Parse(ingress.ToAmount, CultureInfo.InvariantCulture))
            throw new InvalidOperationException("The ingress quote does not fund the exact outgoing lock with the same hash.");
    }

    private async Task<RecoveryState> LoadAsync(ArkCompositionExecutionRequest request, ArkCompositionPreparation prepared)
    {
        var state = await privateStore.ReadAsync<RecoveryState>(request.StoreId, request.RouteId)
            ?? throw new InvalidOperationException("The route has no protected preparation.");
        ValidateRequest(state, request);
        if (state.Preparation != prepared) throw new InvalidOperationException("The RFQ reservations differ from preparation.");
        return state;
    }

    private static void ValidateRequest(RecoveryState state, ArkCompositionExecutionRequest request)
    {
        if (state.Request != JsonSerializer.Serialize(request))
            throw new InvalidOperationException("The route request cannot change after preparation.");
    }

    private Task SaveAsync(ArkCompositionExecutionRequest request, RecoveryState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return privateStore.WriteAsync(request.StoreId, request.RouteId, state);
    }

    private static SwapLinkSecret Secret(RecoveryState state)
    {
        var bytes = Convert.FromHexString(state.Preimage);
        try { return SwapLinkSecret.FromPreimage(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static ArkCompositionQuote Facts<T>(RfqQuote<T> quote, string hash, VHTLCv2Contract contract,
        string address, string? payout = null) => new(quote.RfqId!, hash, quote.SolverPubkey,
            Number(quote.FromAtomicAmount), Number(quote.ToAtomicAmount), contract.GetScriptPubKey().ToHex(),
            address, quote.ValidUntil, quote.RefundLocktime, payout);
    private static string PayoutScript(VHTLCv2Contract contract) => Convert.ToHexString(
        contract.NonInteractiveClaim?.ReceiverPkScript ?? throw new InvalidOperationException("Ingress requires a pinned NI claim.")).ToLowerInvariant();
    private static string Number(System.Numerics.BigInteger amount) => amount.ToString(CultureInfo.InvariantCulture);

    private sealed class RecoveryState
    {
        public RecoveryState() { }
        public required string Request { get; init; }
        public required string Preimage { get; init; }
        public required ArkCompositionPreparation Preparation { get; init; }
        public string? RefundContract { get; set; }
        public string? OutgoingContract { get; set; }
        public string? OnchainRefundAddress { get; set; }
        public ArkEvmSolverSelection? OutgoingSolver { get; set; }
        public ArkEvmSolverSelection? IngressSolver { get; set; }
        public SolverCard? OutgoingCard { get; set; }
        public SolverCard? IngressCard { get; set; }
        public ArkCompositionRfqCheckpointState OutgoingRfq { get; init; } = new();
        public ArkCompositionRfqCheckpointState IngressRfq { get; init; } = new();
        public ArkCompositionOutgoingQuote? Outgoing { get; set; }
        public ArkCompositionIngressQuote? Ingress { get; set; }
        public override string ToString() => nameof(RecoveryState);
    }
}
