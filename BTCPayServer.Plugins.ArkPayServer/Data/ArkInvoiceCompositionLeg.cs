using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

/// <summary>One globally unique prepared RFQ and its immutable public quote/funding facts.</summary>
public sealed class ArkInvoiceCompositionLeg
{
    private ArkInvoiceCompositionLeg() { }

    /// <summary>Global RFQ identity across both leg kinds and every route.</summary>
    [Key]
    public string RfqId { get; private set; } = "";
    /// <summary>Owning route.</summary>
    public Guid RouteId { get; private set; }
    /// <summary>Outgoing or Ingress.</summary>
    public string Kind { get; private set; } = "";
    /// <summary>SDK-validated settlement key.</summary>
    public string? SolverPubkey { get; private set; }
    /// <summary>Quoted atomic input as a canonical integer string.</summary>
    public string? FromAmount { get; private set; }
    /// <summary>Quoted atomic output as a canonical integer string.</summary>
    public string? ToAmount { get; private set; }
    /// <summary>Validated L or M scriptPubKey.</summary>
    public string? LockupScript { get; private set; }
    /// <summary>Arkade address for L or M.</summary>
    public string? LockupAddress { get; private set; }
    /// <summary>Ingress claim destination L.</summary>
    public string? PayoutScript { get; private set; }
    /// <summary>Quote funding deadline, Unix seconds.</summary>
    public long? ValidUntil { get; private set; }
    /// <summary>Arkade refund deadline, Unix seconds.</summary>
    public long? RefundLocktime { get; private set; }
    /// <summary>Transaction whose fully observed output funded this lock.</summary>
    public string? FundingTransactionId { get; private set; }
    /// <summary>Observed Arkade funding amount.</summary>
    public long? FundedAmountSats { get; private set; }

    internal static ArkInvoiceCompositionLeg Prepare(Guid routeId, string rfqId, string kind) =>
        new() { RouteId = routeId, RfqId = rfqId, Kind = kind };

    internal ArkCompositionQuote? Quote(string paymentHash) => FromAmount is null ? null :
        new(RfqId, paymentHash, SolverPubkey!, FromAmount, ToAmount!, LockupScript!, LockupAddress!,
            ValidUntil!.Value, RefundLocktime!.Value, PayoutScript);

    internal void SetQuote(ArkCompositionQuote quote)
    {
        SolverPubkey = quote.SolverPubkey;
        FromAmount = quote.FromAmount;
        ToAmount = quote.ToAmount;
        LockupScript = quote.LockupScript;
        LockupAddress = quote.LockupAddress;
        PayoutScript = quote.PayoutScript;
        ValidUntil = quote.ValidUntil;
        RefundLocktime = quote.RefundLocktime;
    }

    internal void SetFunding(string transactionId, long amountSats)
    {
        FundingTransactionId = transactionId;
        FundedAmountSats = amountSats;
    }
}
