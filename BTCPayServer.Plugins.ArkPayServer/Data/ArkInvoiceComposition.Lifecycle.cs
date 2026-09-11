using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Numerics;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public sealed partial class ArkInvoiceComposition
{
    private readonly List<ArkInvoiceCompositionLeg> _legs = [];

    /// <summary>Monotonic optimistic-concurrency version.</summary>
    public long Revision { get; private set; }
    /// <summary>Independent prepared RFQs; no secret-bearing request bodies.</summary>
    public IReadOnlyCollection<ArkInvoiceCompositionLeg> Legs => _legs.AsReadOnly();
    /// <summary>Base BTC amount expected by L.</summary>
    public long? BaseAmountSats { get; private set; }
    /// <summary>Allowed contract snapshotted from store policy.</summary>
    public string? SwapContractAddress { get; private set; }
    /// <summary>Exact EVM amount from the tuple.</summary>
    public string? EvmAmount { get; private set; }
    /// <summary>Exact tuple token.</summary>
    public string? EvmTokenAddress { get; private set; }
    /// <summary>Exact tuple merchant recipient.</summary>
    public string? EvmClaimAddress { get; private set; }
    /// <summary>Exact tuple expired-lock recipient.</summary>
    public string? EvmRefundAddress { get; private set; }
    /// <summary>Exact tuple refund block.</summary>
    public string? EvmTimeoutBlock { get; private set; }
    /// <summary>Transaction that spent M to create L.</summary>
    public string? IngressClaimTransactionId { get; private set; }
    /// <summary>EVM lock funding transaction when known.</summary>
    public string? EvmLockTransactionId { get; private set; }
    /// <summary>EVM tip used to prove the lock.</summary>
    public string? EvmObservedAtBlock { get; private set; }
    /// <summary>EVM historical block proving the lock.</summary>
    public string? EvmProvenAtBlock { get; private set; }
    /// <summary>Proving block timestamp, Unix seconds.</summary>
    public long? EvmProvenBlockTimestamp { get; private set; }
    /// <summary>Transaction whose canonical events proved delivery.</summary>
    public string? EvmClaimTransactionId { get; private set; }
    /// <summary>Nonsecret recovery condition, independent of money progress.</summary>
    public ArkCompositionFailure? FailureCode { get; private set; }
    /// <summary>Ingress spread paid by the customer; zero for direct Arkade.</summary>
    [NotMapped]
    public long IngressFeeSats => _legs.FirstOrDefault(leg => leg.Kind == "Ingress") is { FromAmount: not null, ToAmount: not null } ingress
        ? checked(ArkCompositionValidation.Sats(ingress.FromAmount) - ArkCompositionValidation.Sats(ingress.ToAmount)) : 0;
    /// <summary>Only verified EVM delivery constitutes completion; no invoice is changed here.</summary>
    [NotMapped]
    public bool SettlementVerified => Status == "EvmClaimVerified";

    /// <summary>Records SDK-generated H and fixed RFQ ids before any remote call.</summary>
    public void Prepare(long baseAmountSats, string paymentHash, string outgoingRfqId, string? ingressRfqId)
    {
        var hash = ArkCompositionValidation.Hash(paymentHash);
        var outgoing = ArkCompositionValidation.Hash(outgoingRfqId, true);
        var ingress = ingressRfqId is null ? null : ArkCompositionValidation.Hash(ingressRfqId, true);
        Require(baseAmountSats > 0 && SwapContractAddress is not null &&
                (PaymentMethodId == "ARKADE" ? ingress is null : ingress is not null) && outgoing != ingress &&
                (PaymentHash is null || PaymentHash == hash));
        if (BaseAmountSats is not null)
        {
            Require(BaseAmountSats == baseAmountSats && PaymentHash == hash && Leg("Outgoing").RfqId == outgoing &&
                    _legs.FirstOrDefault(leg => leg.Kind == "Ingress")?.RfqId == ingress);
            return;
        }
        RequireStatus("PendingSdk");
        Advance("Prepared");
        BaseAmountSats = baseAmountSats;
        PaymentHash = hash;
        _legs.Add(ArkInvoiceCompositionLeg.Prepare(RouteId, outgoing, "Outgoing"));
        if (ingress is not null) _legs.Add(ArkInvoiceCompositionLeg.Prepare(RouteId, ingress, "Ingress"));
    }
    /// <summary>Records the first quote and exact EVM tuple.</summary>
    public void RecordOutgoingQuote(ArkCompositionQuote quote, ArkCompositionEvmTerms terms)
    {
        quote = ArkCompositionValidation.Quote(quote);
        terms = ArkCompositionValidation.Terms(terms);
        var leg = Leg("Outgoing");
        Require(quote.RfqId == leg.RfqId && quote.PaymentHash == PaymentHash && quote.PayoutScript is null &&
                ArkCompositionValidation.Sats(quote.FromAmount) == BaseAmountSats && terms.PaymentHash == PaymentHash &&
                terms.Amount == quote.ToAmount && terms.TokenAddress == AssetId[(AssetId.LastIndexOf(':') + 1)..] &&
                terms.ClaimAddress == Destination && terms.SwapContractAddress == SwapContractAddress);
        if (leg.FromAmount is not null)
        {
            Require(leg.Quote(PaymentHash!) == quote && StoredEvmTerms() == terms);
            return;
        }
        RequireStatus("Prepared");
        Advance("OutgoingQuoted");
        leg.SetQuote(quote);
        EvmAmount = terms.Amount;
        EvmTokenAddress = terms.TokenAddress;
        EvmClaimAddress = terms.ClaimAddress;
        EvmRefundAddress = terms.RefundAddress;
        EvmTimeoutBlock = terms.TimeoutBlock;
    }
    /// <summary>Records ingress whose NI claim pays exactly L using the same H.</summary>
    public void RecordIngressQuote(ArkCompositionQuote quote)
    {
        Require(PaymentMethodId != "ARKADE");
        quote = ArkCompositionValidation.Quote(quote);
        var leg = Leg("Ingress");
        Require(quote.RfqId == leg.RfqId && quote.PaymentHash == PaymentHash &&
                ArkCompositionValidation.Sats(quote.ToAmount) == BaseAmountSats &&
                ArkCompositionValidation.Sats(quote.FromAmount) >= BaseAmountSats &&
                quote.PayoutScript == Leg("Outgoing").LockupScript);
        if (leg.FromAmount is not null)
        {
            Require(leg.Quote(PaymentHash!) == quote);
            return;
        }
        RequireStatus("OutgoingQuoted");
        Advance("IngressQuoted");
        leg.SetQuote(quote);
    }
    /// <summary>Records exact quoted M funding, not customer-source payment.</summary>
    public void RecordIngressLockupFunded(string transactionId, long amountSats)
    {
        Require(PaymentMethodId != "ARKADE");
        RecordFunding("Ingress", transactionId, amountSats, "IngressQuoted", "IngressLockupFunded");
    }
    /// <summary>Records the M-to-L transaction without implying that L is observed funded.</summary>
    public void RecordIngressClaimedToOutgoing(string transactionId)
    {
        Require(PaymentMethodId != "ARKADE");
        var txId = ArkCompositionValidation.Hash(transactionId);
        if (IngressClaimTransactionId is not null)
        {
            Require(IngressClaimTransactionId == txId);
            return;
        }
        RequireStatus("IngressLockupFunded");
        Advance("IngressClaimedToOutgoing");
        IngressClaimTransactionId = txId;
    }
    /// <summary>Records exact quoted L funding independently of an M-to-L submission.</summary>
    public void RecordOutgoingLockupFunded(string transactionId, long amountSats)
    {
        var txId = ArkCompositionValidation.Hash(transactionId);
        Require(PaymentMethodId == "ARKADE" || txId == IngressClaimTransactionId);
        RecordFunding("Outgoing", txId, amountSats,
            PaymentMethodId == "ARKADE" ? "OutgoingQuoted" : "IngressClaimedToOutgoing", "OutgoingLockupFunded");
    }
    /// <summary>Records an SDK-verified public EVM lock proof.</summary>
    public void RecordEvmLockProven(ArkCompositionEvmLockProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        proof = proof with
        {
            TransactionId = proof.TransactionId is null ? null : ArkCompositionValidation.EvmTransaction(proof.TransactionId),
            ObservedAtBlock = ArkCompositionValidation.Integer(proof.ObservedAtBlock, true),
            ProvenAtBlock = ArkCompositionValidation.Integer(proof.ProvenAtBlock, true),
            ProvenBlockTimestamp = ArkCompositionValidation.Timestamp(proof.ProvenBlockTimestamp)
        };
        Require(BigInteger.Parse(proof.ObservedAtBlock, CultureInfo.InvariantCulture) >= BigInteger.Parse(proof.ProvenAtBlock, CultureInfo.InvariantCulture));
        if (EvmObservedAtBlock is not null)
        {
            Require(new ArkCompositionEvmLockProof(EvmLockTransactionId, EvmObservedAtBlock,
                EvmProvenAtBlock!, EvmProvenBlockTimestamp!.Value) == proof);
            return;
        }
        RequireStatus("OutgoingLockupFunded");
        Advance("EvmLockProven");
        EvmLockTransactionId = proof.TransactionId;
        EvmObservedAtBlock = proof.ObservedAtBlock;
        EvmProvenAtBlock = proof.ProvenAtBlock;
        EvmProvenBlockTimestamp = proof.ProvenBlockTimestamp;
    }
    /// <summary>Records SDK-verified canonical claim and delivery events without accepting P.</summary>
    public void RecordEvmClaimVerified(string transactionId, string deliveredAmount)
    {
        var txId = ArkCompositionValidation.EvmTransaction(transactionId);
        Require(ArkCompositionValidation.Integer(deliveredAmount) == EvmAmount);
        if (EvmClaimTransactionId is not null)
        {
            Require(EvmClaimTransactionId == txId);
            return;
        }
        RequireStatus("EvmLockProven");
        Advance("EvmClaimVerified");
        EvmClaimTransactionId = txId;
    }
    /// <summary>Records only a closed, nonsecret recovery code; never completes a payment.</summary>
    public void RecordFailure(ArkCompositionFailure failure)
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentException("Unknown composition failure code.");
        if (FailureCode == failure) return;
        Require(!SettlementVerified);
        Revision = checked(Revision + 1);
        FailureCode = failure;
    }

    private void RecordFunding(string kind, string transactionId, long amountSats, string requiredState, string nextState)
    {
        var txId = ArkCompositionValidation.Hash(transactionId);
        var leg = Leg(kind);
        Require(leg.FromAmount is not null && amountSats == ArkCompositionValidation.Sats(
            kind == "Ingress" ? leg.ToAmount! : leg.FromAmount));
        if (leg.FundingTransactionId is not null)
        {
            Require(leg.FundingTransactionId == txId && leg.FundedAmountSats == amountSats);
            return;
        }
        RequireStatus(requiredState);
        Advance(nextState);
        leg.SetFunding(txId, amountSats);
    }

    private ArkCompositionEvmTerms StoredEvmTerms() => new(PaymentHash!, EvmAmount!, EvmTokenAddress!,
        EvmClaimAddress!, EvmRefundAddress!, EvmTimeoutBlock!, SwapContractAddress!);

    private ArkInvoiceCompositionLeg Leg(string kind) => _legs.FirstOrDefault(leg => leg.Kind == kind)
        ?? throw new InvalidOperationException("The route has not prepared this leg.");

    private void RequireStatus(string status) => Require(Status == status);

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("The public facts conflict with this route or its required state.");
    }

    private void Advance(string status)
    {
        Revision = checked(Revision + 1);
        Status = status;
        FailureCode = null;
    }
}
