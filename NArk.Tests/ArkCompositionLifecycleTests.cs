using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using NArk.Abstractions;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionLifecycleTests
{
    [Theory]
    [InlineData("ARKADE", false, 999)]
    [InlineData("ARKADE", false, 1001)]
    [InlineData("BTC-LN", true, 999)]
    [InlineData("BTC-LN", true, 1001)]
    [InlineData("BTC-CHAIN", true, 999)]
    [InlineData("BTC-CHAIN", true, 1001)]
    [InlineData("BTC-LN", false, 999)]
    [InlineData("BTC-LN", false, 1001)]
    [InlineData("BTC-CHAIN", false, 999)]
    [InlineData("BTC-CHAIN", false, 1001)]
    public void EachLockRequiresItsExactQuotedAmountNotUnderfundingOrOverfunding(string rail, bool ingress, long amount)
    {
        var route = CompositionFixture.Prepared(rail);
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        if (rail != "ARKADE")
        {
            route.RecordIngressQuote(CompositionFixture.IngressQuote());
            if (!ingress)
            {
                route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 1000);
                route.RecordIngressClaimedToOutgoing(CompositionFixture.ClaimTx);
            }
        }
        var before = JsonConvert.SerializeObject(route);

        Assert.Throws<InvalidOperationException>(() => Record(amount));

        Assert.Equal(before, JsonConvert.SerializeObject(route));
        Assert.False(route.SettlementVerified);
        Record(1000);
        Assert.Equal(ingress ? "IngressLockupFunded" : "OutgoingLockupFunded", route.Status);
        void Record(long observedAmount)
        {
            if (ingress) route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, observedAmount);
            else route.RecordOutgoingLockupFunded(CompositionFixture.ClaimTx, observedAmount);
        }
    }

    [Theory]
    [InlineData("ARKADE")]
    [InlineData("BTC-LN")]
    [InlineData("BTC-CHAIN")]
    public void EveryStateAcceptsOnlyItsNextStepOrAnExactPriorReplay(string rail)
    {
        var steps = rail == "ARKADE" ? new[] { 0, 4, 5, 6 } : new[] { 0, 1, 2, 3, 4, 5, 6 };
        for (var completed = 0; completed <= steps.Length; completed++)
            for (var candidate = 0; candidate < steps.Length; candidate++)
            {
                var route = CompositionFixture.Prepared(rail);
                for (var prior = 0; prior < completed; prior++) Apply(route, steps[prior]);
                var revision = route.Revision;
                var status = route.Status;
                if (candidate > completed)
                    Assert.Throws<InvalidOperationException>(() => Apply(route, steps[candidate]));
                else
                    Apply(route, steps[candidate]);
                Assert.Equal(revision + (candidate == completed ? 1 : 0), route.Revision);
                if (candidate != completed) Assert.Equal(status, route.Status);
                Assert.Equal(route.Status == "EvmClaimVerified", route.SettlementVerified);
            }

        static void Apply(ArkInvoiceComposition route, int step)
        {
            switch (step)
            {
                case 0: route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms()); break;
                case 1: route.RecordIngressQuote(CompositionFixture.IngressQuote()); break;
                case 2: route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 1000); break;
                case 3: route.RecordIngressClaimedToOutgoing(CompositionFixture.ClaimTx); break;
                case 4: route.RecordOutgoingLockupFunded(CompositionFixture.ClaimTx, 1000); break;
                case 5: route.RecordEvmLockProven(CompositionFixture.LockProof()); break;
                case 6: route.RecordEvmClaimVerified(CompositionFixture.EvmClaimTx, "2000000"); break;
            }
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData("1.5")]
    [InlineData("secret-amount")]
    [InlineData("115792089237316195423570985008687907853269984665640564039457584007913129639936")]
    public void InvalidExactAmountsCannotChangeOrLeakIntoTheJournal(string amount)
    {
        var route = CompositionFixture.Prepared();
        var before = JsonConvert.SerializeObject(route);
        var error = Assert.Throws<ArgumentException>(() => route.RecordOutgoingQuote(
            CompositionFixture.OutgoingQuote() with { ToAmount = amount }, CompositionFixture.EvmTerms() with { Amount = amount }));
        Assert.DoesNotContain("secret-amount", error.ToString());
        Assert.Equal(before, JsonConvert.SerializeObject(route));
    }

    [Fact]
    public void MalformedQuoteKeysAddressesAndDeadlinesAreRejectedWithoutEchoingInput()
    {
        foreach (var quote in new[]
                 {
                     CompositionFixture.OutgoingQuote() with { SolverPubkey = new string('0', 64) },
                     CompositionFixture.OutgoingQuote() with { LockupAddress = "secret-address" },
                     CompositionFixture.OutgoingQuote() with { LockupScript = CompositionFixture.MScript },
                     CompositionFixture.OutgoingQuote() with { ValidUntil = -1 },
                     CompositionFixture.OutgoingQuote() with { RefundLocktime = 1800000060 },
                     CompositionFixture.OutgoingQuote() with { RfqId = CompositionFixture.OutgoingRfq.ToUpperInvariant() }
                 })
        {
            var route = CompositionFixture.Prepared();
            var before = JsonConvert.SerializeObject(route);
            var error = Assert.Throws<ArgumentException>(() => route.RecordOutgoingQuote(quote, CompositionFixture.EvmTerms()));
            Assert.DoesNotContain("secret-address", error.ToString());
            Assert.Equal(before, JsonConvert.SerializeObject(route));
        }
    }

    [Theory]
    [InlineData("BTC-LN")]
    [InlineData("BTC-CHAIN")]
    public void IngressRouteRequiresEveryObservedMoneyStepBeforeCompletion(string rail)
    {
        var route = CompositionFixture.Prepared(rail);
        Assert.Equal("Prepared", route.Status);
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        Assert.Equal("OutgoingQuoted", route.Status);
        route.RecordIngressQuote(CompositionFixture.IngressQuote());
        Assert.Equal("IngressQuoted", route.Status);
        Assert.Equal(25, route.IngressFeeSats);
        route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 1000);
        Assert.Equal("IngressLockupFunded", route.Status);
        Assert.False(route.SettlementVerified);
        route.RecordIngressClaimedToOutgoing(CompositionFixture.ClaimTx);
        Assert.Equal("IngressClaimedToOutgoing", route.Status);
        route.RecordOutgoingLockupFunded(CompositionFixture.ClaimTx, 1000);
        Assert.Equal("OutgoingLockupFunded", route.Status);
        route.RecordEvmLockProven(CompositionFixture.LockProof());
        Assert.Equal("EvmLockProven", route.Status);
        Assert.False(route.SettlementVerified);
        route.RecordEvmClaimVerified(CompositionFixture.EvmClaimTx, "2000000");
        Assert.Equal("EvmClaimVerified", route.Status);
        Assert.True(route.SettlementVerified);
    }

    [Fact]
    public void DirectArkadeSkipsOnlyTheIngressSteps()
    {
        var route = CompositionFixture.Prepared("ARKADE");
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        Assert.Throws<InvalidOperationException>(() => route.RecordIngressQuote(CompositionFixture.IngressQuote()));
        Assert.Throws<InvalidOperationException>(() => route.RecordEvmLockProven(CompositionFixture.LockProof()));
        route.RecordOutgoingLockupFunded(CompositionFixture.ClaimTx, 1000);
        route.RecordEvmLockProven(CompositionFixture.LockProof());
        route.RecordEvmClaimVerified(CompositionFixture.EvmClaimTx, "2000000");
        Assert.True(route.SettlementVerified);
        Assert.Single(route.Legs);
        Assert.Equal(0, route.IngressFeeSats);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void FutureTransitionsCannotSkipMissingMoneyEvidence(int step)
    {
        var route = CompositionFixture.Prepared();
        Action[] transitions =
        [
            () => route.RecordIngressQuote(CompositionFixture.IngressQuote()),
            () => route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 1000),
            () => route.RecordIngressClaimedToOutgoing(CompositionFixture.ClaimTx),
            () => route.RecordOutgoingLockupFunded(CompositionFixture.ClaimTx, 1000),
            () => route.RecordEvmLockProven(CompositionFixture.LockProof()),
            () => route.RecordEvmClaimVerified(CompositionFixture.EvmClaimTx, "2000000"),
            () => route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote() with { PaymentHash = new string('f', 64) }, CompositionFixture.EvmTerms())
        ];
        var version = route.Revision;

        Assert.ThrowsAny<InvalidOperationException>(transitions[step]);

        Assert.Equal("Prepared", route.Status);
        Assert.Equal(version, route.Revision);
        Assert.False(route.SettlementVerified);
    }

    [Fact]
    public void ExactRepeatedFactsRemainIdempotentEvenAfterLaterTransitions()
    {
        var route = CompositionFixture.Completed();
        var version = route.Revision;

        route.Prepare(1000, CompositionFixture.Hash, CompositionFixture.OutgoingRfq, CompositionFixture.IngressRfq);
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        route.RecordIngressQuote(CompositionFixture.IngressQuote());
        route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 1000);
        route.RecordIngressClaimedToOutgoing(CompositionFixture.ClaimTx);
        route.RecordOutgoingLockupFunded(CompositionFixture.ClaimTx, 1000);
        route.RecordEvmLockProven(CompositionFixture.LockProof());
        route.RecordEvmClaimVerified(CompositionFixture.EvmClaimTx, "2000000");

        Assert.Equal(version, route.Revision);
        Assert.Equal("EvmClaimVerified", route.Status);
    }

    [Fact]
    public void ExistingQuoteAndTransactionFactsCannotBeReplaced()
    {
        var route = CompositionFixture.Completed();
        var version = route.Revision;

        Assert.Throws<InvalidOperationException>(() => route.Prepare(1001, CompositionFixture.Hash, CompositionFixture.OutgoingRfq, CompositionFixture.IngressRfq));
        Assert.Throws<InvalidOperationException>(() => route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote() with { ToAmount = "3000000" }, CompositionFixture.EvmTerms()));
        Assert.Throws<InvalidOperationException>(() => route.RecordIngressQuote(CompositionFixture.IngressQuote() with { FromAmount = "1026" }));
        Assert.Throws<InvalidOperationException>(() => route.RecordIngressClaimedToOutgoing(new string('f', 64)));
        Assert.Throws<InvalidOperationException>(() => route.RecordEvmClaimVerified("0x" + new string('f', 64), "2000000"));
        Assert.Equal(version, route.Revision);
    }

    [Fact]
    public void IngressMustUseSameHashExactOutgoingAmountAndPayoutScript()
    {
        var route = CompositionFixture.Prepared();
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        foreach (var quote in new[]
                 {
                     CompositionFixture.IngressQuote() with { PaymentHash = new string('f', 64) },
                     CompositionFixture.IngressQuote() with { ToAmount = "999" },
                     CompositionFixture.IngressQuote() with { PayoutScript = CompositionFixture.MScript },
                     CompositionFixture.IngressQuote() with { FromAmount = "999" }
                 })
            Assert.Throws<InvalidOperationException>(() => route.RecordIngressQuote(quote));
        Assert.Equal("OutgoingQuoted", route.Status);
    }

    [Fact]
    public void UnderfundingOrAnUnrelatedOutgoingTransactionDoesNotAdvance()
    {
        var route = CompositionFixture.Prepared();
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        route.RecordIngressQuote(CompositionFixture.IngressQuote());
        Assert.Throws<InvalidOperationException>(() => route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 999));
        route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 1000);
        route.RecordIngressClaimedToOutgoing(CompositionFixture.ClaimTx);
        Assert.Throws<InvalidOperationException>(() => route.RecordOutgoingLockupFunded(new string('f', 64), 1000));
        Assert.Throws<InvalidOperationException>(() => route.RecordOutgoingLockupFunded(CompositionFixture.ClaimTx, 999));
        Assert.False(route.SettlementVerified);
    }

    [Fact]
    public void EvmTermsMustMatchHashAssetDestinationContractAndQuotedAmount()
    {
        foreach (var terms in new[]
                 {
                     CompositionFixture.EvmTerms() with { PaymentHash = new string('f', 64) },
                     CompositionFixture.EvmTerms() with { Amount = "1" },
                     CompositionFixture.EvmTerms() with { TokenAddress = CompositionFixture.RefundAddress },
                     CompositionFixture.EvmTerms() with { ClaimAddress = CompositionFixture.RefundAddress },
                     CompositionFixture.EvmTerms() with { SwapContractAddress = CompositionFixture.RefundAddress }
                 })
        {
            var route = CompositionFixture.Prepared();
            Assert.Throws<InvalidOperationException>(() => route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), terms));
            Assert.Equal("Prepared", route.Status);
        }
    }

    [Fact]
    public void Uint256ValuesRemainExactAndDoNotUseFloatingPoint()
    {
        const string amount = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
        var route = CompositionFixture.Prepared("ARKADE");
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote() with { ToAmount = amount }, CompositionFixture.EvmTerms() with { Amount = amount });
        Assert.Equal(amount, route.Legs.Single().ToAmount);
        Assert.Equal(amount, route.EvmAmount);
    }

    [Fact]
    public void FailureCodesCannotCompleteOrEraseObservedMoneyState()
    {
        var route = CompositionFixture.Prepared();
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        route.RecordFailure(ArkCompositionFailure.RemoteUnavailable);
        var version = route.Revision;
        route.RecordFailure(ArkCompositionFailure.RemoteUnavailable);
        Assert.Equal(version, route.Revision);
        Assert.Equal("OutgoingQuoted", route.Status);
        Assert.Equal(ArkCompositionFailure.RemoteUnavailable, route.FailureCode);
        Assert.False(route.SettlementVerified);
        Assert.Throws<ArgumentException>(() => route.RecordFailure((ArkCompositionFailure)999));
    }

    [Fact]
    public void AttachmentIsAnIndependentIdempotentRevisionAndCannotCrossStores()
    {
        var route = CompositionFixture.Prepared();
        var invoice = new InvoiceEntity { Id = "invoice", StoreId = "store" };
        route.AttachInvoice(invoice, PaymentMethodId.Parse("BTC-LN"), CompositionFixture.Hash);
        var version = route.Revision;
        route.AttachInvoice(invoice, PaymentMethodId.Parse("BTC-LN"), CompositionFixture.Hash);
        Assert.Equal(version, route.Revision);
        Assert.Throws<InvalidOperationException>(() => route.AttachInvoice(
            new InvoiceEntity { Id = "invoice", StoreId = "other-store" }, PaymentMethodId.Parse("BTC-LN"), CompositionFixture.Hash));
        Assert.Equal("Prepared", route.Status);
    }
}

internal static class CompositionFixture
{
    internal const string Key = "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    internal const string OtherKey = "c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";
    internal const string Asset = "eip155:42161/erc20:0x1111111111111111111111111111111111111111";
    internal const string Destination = "0x2222222222222222222222222222222222222222";
    internal const string Contract = "0x3333333333333333333333333333333333333333";
    internal const string RefundAddress = "0x4444444444444444444444444444444444444444";
    internal static readonly string Hash = new('a', 64);
    internal static readonly string OutgoingRfq = new('b', 64);
    internal static readonly string IngressRfq = new('c', 64);
    internal static readonly string MFundingTx = new('d', 64);
    internal static readonly string ClaimTx = new('e', 64);
    internal static readonly string EvmClaimTx = "0x" + new string('a', 64);
    internal const string LScript = "5120" + Key;
    internal const string MScript = "5120" + OtherKey;

    internal static ArkEvmSettlementSettings Settings() => new(Asset, Destination, true)
    {
        RoutePolicy = new ArkEvmRoutePolicy
        {
            EnabledSourceRails = ["ARKADE", "BTC-LN", "BTC-CHAIN"],
            OutgoingSolver = new("registry"),
            LightningIngressSolver = new("registry"),
            OnchainIngressSolver = new("registry"),
            SwapContractAddress = Contract,
            FastestSecondsPerBlock = 1,
            SlowestSecondsPerBlock = 2,
            MinConfirmations = 3,
            MinAgeSeconds = 5,
            MinimumClaimWindowSeconds = 1800,
            ArkadeRefundMarginSeconds = 7200
        }
    };

    internal static ArkInvoiceComposition Create(string rail = "BTC-LN", string storeId = "store")
    {
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, null!);
        var store = new StoreData { Id = storeId };
        store.SetPaymentMethodConfig(handler, new ArkadePaymentMethodConfig("private-wallet") { EvmSettlement = Settings() });
        return ArkInvoiceComposition.Create(store, null, new PaymentMethodHandlerDictionary([handler]),
            DateTimeOffset.FromUnixTimeSeconds(1800000000), PaymentMethodId.Parse(rail));
    }

    internal static ArkInvoiceComposition Prepared(string rail = "BTC-LN")
    {
        var route = Create(rail);
        route.Prepare(1000, Hash, OutgoingRfq, rail == "ARKADE" ? null : IngressRfq);
        return route;
    }

    internal static ArkCompositionQuote OutgoingQuote() =>
        new(OutgoingRfq, Hash, Key, "1000", "2000000", LScript, Address(Key), 1800000060, 1800020000);
    internal static ArkCompositionQuote IngressQuote() =>
        new(IngressRfq, Hash, Key, "1025", "1000", MScript, Address(OtherKey), 1800000050, 1800010000, LScript);
    internal static ArkCompositionEvmTerms EvmTerms() =>
        new(Hash, "2000000", "0x1111111111111111111111111111111111111111", Destination, RefundAddress, "2000000", Contract);
    internal static ArkCompositionEvmLockProof LockProof() => new("0x" + new string('b', 64), "1900002", "1900000", 1800000100);

    internal static ArkInvoiceComposition Completed()
    {
        var route = Prepared();
        route.RecordOutgoingQuote(OutgoingQuote(), EvmTerms());
        route.RecordIngressQuote(IngressQuote());
        route.RecordIngressLockupFunded(MFundingTx, 1000);
        route.RecordIngressClaimedToOutgoing(ClaimTx);
        route.RecordOutgoingLockupFunded(ClaimTx, 1000);
        route.RecordEvmLockProven(LockProof());
        route.RecordEvmClaimVerified(EvmClaimTx, "2000000");
        return route;
    }

    private static string Address(string key) => new ArkAddress(ECXOnlyPubKey.Create(Convert.FromHexString(key)),
        ECXOnlyPubKey.Create(Convert.FromHexString(Key))).ToString(false);
}
