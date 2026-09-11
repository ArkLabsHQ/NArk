using BTCPayServer.Client.Models;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Onchain;
using NArk.Core;
using NArk.Core.Transport;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionExecutionTests
{
    [Theory]
    [InlineData("ARKADE")]
    [InlineData("BTC-LN")]
    [InlineData("BTC-CHAIN")]
    public async Task FundingAndIngressClaimNeverSettleButExactEvmReceiptDoes(string rail)
    {
        await using var database = await CompositionDatabase.Create();
        var route = Ready(rail);
        await database.Repository.Add("store", route);
        var backend = new Backend(rail);
        var sink = new Sink(database);
        var journal = new ArkCompositionExecutionJournal(database, database.Repository);
        var execution = new ArkCompositionExecutionService(journal, backend, new ExecutionLock(), sink);

        await execution.AdvanceAsync("store", route.RouteId);
        Assert.Empty(sink.Settlements);
        Assert.Equal("OutgoingLockupFunded", (await database.Repository.Get("store", route.RouteId))!.Status);
        backend.Result = new(CompositionFixture.LockProof());
        await execution.AdvanceAsync("store", route.RouteId);
        Assert.Empty(sink.Settlements);
        backend.Result = new(CompositionFixture.LockProof(), CompositionFixture.EvmClaimTx, "2000000");
        await execution.AdvanceAsync("store", route.RouteId);
        Assert.Single(sink.Settlements);
        Assert.True((await database.Repository.Get("store", route.RouteId))!.SettlementVerified);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1001)]
    public async Task InexactMPreventsAnySdkExecutionOrPayment(long amount)
    {
        await using var database = await CompositionDatabase.Create();
        var route = Ready("BTC-LN");
        await database.Repository.Add("store", route);
        var backend = new Backend("BTC-LN") { FundingAmount = amount };
        var sink = new Sink(database);
        var execution = new ArkCompositionExecutionService(new ArkCompositionExecutionJournal(database, database.Repository),
            backend, new ExecutionLock(), sink);

        await Assert.ThrowsAsync<InvalidOperationException>(() => execution.AdvanceAsync("store", route.RouteId));

        Assert.Equal(0, backend.Advances);
        Assert.Empty(sink.Settlements);
        Assert.Equal("IngressQuoted", (await database.Repository.Get("store", route.RouteId))!.Status);
    }

    [Fact]
    public async Task RestartAfterPreparedBroadcastFailureRecoversSameSdkReceiptWithoutPrematureSettlement()
    {
        await using var database = await CompositionDatabase.Create();
        var route = Ready("BTC-LN");
        await database.Repository.Add("store", route);
        var backend = new Backend("BTC-LN") { Failure = new Exception("secret-preimage-must-not-escape") };
        var sink = new Sink(database);
        var journal = new ArkCompositionExecutionJournal(database, database.Repository);
        var execution = new ArkCompositionExecutionService(journal, backend, new ExecutionLock(), sink);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => execution.AdvanceAsync("store", route.RouteId));
        Assert.DoesNotContain("secret-preimage", error.ToString());
        Assert.Empty(sink.Settlements);
        Assert.False((await database.Repository.Get("store", route.RouteId))!.SettlementVerified);
        backend.Failure = null;
        backend.Result = new(CompositionFixture.LockProof(), CompositionFixture.EvmClaimTx, "2000000");
        await new ArkCompositionExecutionService(journal, backend, new ExecutionLock(), sink).AdvanceAsync("store", route.RouteId);

        Assert.Single(sink.Settlements);
        Assert.Equal(CompositionFixture.EvmClaimTx, (await database.Repository.Get("store", route.RouteId))!.EvmClaimTransactionId);
    }

    [Fact]
    public async Task FailedPaymentInsertionRetriesFromVerifiedJournalWithoutExecutingSdkAgain()
    {
        await using var database = await CompositionDatabase.Create();
        var route = Ready("ARKADE");
        await database.Repository.Add("store", route);
        var backend = new Backend("ARKADE") { Result = new(CompositionFixture.LockProof(), CompositionFixture.EvmClaimTx, "2000000") };
        var sink = new Sink(database) { FailOnce = true };
        var journal = new ArkCompositionExecutionJournal(database, database.Repository);
        var executionLock = new ExecutionLock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ArkCompositionExecutionService(journal, backend, executionLock, sink)
            .AdvanceAsync("store", route.RouteId));
        Assert.True((await database.Repository.Get("store", route.RouteId))!.SettlementVerified);
        var resumed = new ArkCompositionExecutionService(journal, backend, executionLock, sink);
        await Task.WhenAll(resumed.AdvanceAsync("store", route.RouteId), resumed.AdvanceAsync("store", route.RouteId));

        Assert.Single(sink.Settlements);
        Assert.Equal(1, backend.Advances);
        Assert.Equal(1, executionLock.MaxConcurrency);
    }

    [Theory]
    [InlineData("1999999")]
    [InlineData("2000001")]
    public async Task WrongEvmReceiptAmountCannotBePersistedOrPaid(string delivered)
    {
        await using var database = await CompositionDatabase.Create();
        var route = Ready("ARKADE");
        await database.Repository.Add("store", route);
        var sink = new Sink(database);
        var backend = new Backend("ARKADE") { Result = new(CompositionFixture.LockProof(), CompositionFixture.EvmClaimTx, delivered) };
        var execution = new ArkCompositionExecutionService(new ArkCompositionExecutionJournal(database, database.Repository),
            backend, new ExecutionLock(), sink);

        await Assert.ThrowsAsync<InvalidOperationException>(() => execution.AdvanceAsync("store", route.RouteId));

        Assert.False((await database.Repository.Get("store", route.RouteId))!.SettlementVerified);
        Assert.Empty(sink.Settlements);
    }

    [Fact]
    public async Task OutgoingRefundRetainsUnsettledInvoice()
    {
        await using var database = await CompositionDatabase.Create();
        var route = Ready("ARKADE");
        await database.Repository.Add("store", route);
        var sink = new Sink(database);
        var backend = new Backend("ARKADE") { Result = new(Refunded: true) };
        await new ArkCompositionExecutionService(new ArkCompositionExecutionJournal(database, database.Repository),
            backend, new ExecutionLock(), sink).AdvanceAsync("store", route.RouteId);

        Assert.Equal(ArkCompositionFailure.RefundRequired, (await database.Repository.Get("store", route.RouteId))!.FailureCode);
        Assert.Empty(sink.Settlements);
    }

    [Theory]
    [InlineData("ARKADE")]
    [InlineData("BTC-LN")]
    [InlineData("BTC-CHAIN")]
    public void PaymentDataUsesExactSourceRailAndContainsOnlyPublicSettlementProof(string rail)
    {
        var route = Ready(rail);
        FundAndComplete(route);
        var invoice = Invoice(route);
        var funding = new ArkCompositionSourceFunding(route.StoreId, route.RouteId, route.PaymentHash!, route.CustomerDestination!,
            [new(CompositionFixture.MFundingTx, 2, 1025)]);
        var payments = ArkCompositionPaymentSink.CreatePayments(route, invoice, new Handler(rail), funding);

        var payment = Assert.Single(payments);
        Assert.Equal(rail, payment.PaymentMethodId);
        Assert.Equal(PaymentStatus.Settled, payment.Status);
        Assert.Equal(rail == "ARKADE" ? 0.00001000m : 0.00001025m, payment.Amount);
        Assert.DoesNotContain("preimage", payment.Blob2, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CompositionFixture.EvmClaimTx, payment.Blob2);
        Assert.Equal(payment.Id, Assert.Single(ArkCompositionPaymentSink.CreatePayments(route, invoice, new Handler(rail), funding)).Id);
        if (rail == "BTC-CHAIN") Assert.Equal(CompositionFixture.MFundingTx + "-2", payment.Id);
    }

    [Fact]
    public void OnchainPaymentsRequireActualSourceEvidenceAndUnverifiedRoutesCannotProducePaymentData()
    {
        var route = Ready("BTC-CHAIN");
        Assert.Throws<InvalidOperationException>(() => ArkCompositionPaymentSink.CreatePayments(route, Invoice(route), new Handler("BTC-CHAIN")));
        FundAndComplete(route);
        Assert.Throws<InvalidOperationException>(() => ArkCompositionPaymentSink.CreatePayments(route, Invoice(route), new Handler("BTC-CHAIN")));
    }

    [Theory]
    [InlineData("ARKADE")]
    [InlineData("BTC-LN")]
    [InlineData("BTC-CHAIN")]
    public void AVerifiedOldRouteSettlesAgainstItsOriginalDestinationAfterPromptRenewal(string rail)
    {
        var route = Ready(rail);
        FundAndComplete(route);
        var invoice = Invoice(route);
        var method = PaymentMethodId.Parse(rail);
        var prompts = invoice.GetPaymentPrompts();
        prompts[method].Destination = "renewed123";
        invoice.SetPaymentPrompts(prompts);
        var source = new ArkCompositionSourceFunding(route.StoreId, route.RouteId, route.PaymentHash!, route.CustomerDestination!,
            [new(CompositionFixture.MFundingTx, 2, 1025)]);

        var payment = Assert.Single(ArkCompositionPaymentSink.CreatePayments(route, invoice, new Handler(rail), source));

        var blob = JObject.Parse(payment.Blob2);
        Assert.Equal(route.CustomerDestination, blob["destination"]!.Value<string>());
        Assert.Equal(route.CustomerDestination, blob["details"]!["destination"]!.Value<string>());
        Assert.Equal("renewed123", invoice.GetPaymentPrompt(method)!.Destination);
        Assert.Equal(payment.Id, Assert.Single(ArkCompositionPaymentSink.CreatePayments(route, invoice, new Handler(rail), source)).Id);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1001)]
    public async Task RealSdkAdapterRejectsInexactObservedMBeforeAnyAction(long amount)
    {
        var route = Ready("BTC-LN");
        var adapter = Adapter(route, amount, out _);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ObserveAsync(route, CancellationToken.None));
    }

    [Fact]
    public async Task RealSdkAdapterReadsBothIndependentIntentsAndPreservesExactMToLTransactionLink()
    {
        var route = Ready("BTC-LN");
        var adapter = Adapter(route, 1000, out var outgoing);
        var observation = await adapter.ObserveAsync(route, CancellationToken.None);
        Assert.Equal(1000, observation.IngressFunding!.AmountSats);
        Assert.Equal(CompositionFixture.ClaimTx, observation.IngressClaimTransactionId);
        Assert.Equal(observation.IngressClaimTransactionId, observation.OutgoingFunding!.TransactionId);
        outgoing.Metadata[ArkadeSwapMetadataKeys.EvmClaimAddress] = CompositionFixture.RefundAddress;
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ObserveAsync(route, CancellationToken.None));
    }

    [Fact]
    public async Task SourceFundingPersistsRealOutpointsAndCanRecoverAfterTheSourceWasSpent()
    {
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var route = Ready("BTC-CHAIN", destination.ToString());
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new OutPoint(uint256.One, 0));
        transaction.Outputs.Add(Money.Satoshis(1025), destination);
        var point = new OutPoint(transaction.GetHash(), 0);
        var settings = new Settings();
        var chain = TestProxy.Create<IBitcoinBlockchain>((method, _) => method.Name switch
        {
            "GetRawTransactionAsync" => Task.FromResult<Transaction?>(transaction),
            "GetUtxosAsync" => Task.FromResult<IReadOnlyList<BoardingUtxo>>([]),
            _ => throw new NotSupportedException(method.Name)
        });
        var transport = TestProxy.Create<IClientTransport>((method, _) => method.Name == "GetServerInfoAsync"
            ? Task.FromResult(new ArkServerInfo(Money.Zero, null!, [], Network.RegTest, new Sequence(0),
                new Sequence(0), null!, null!, null!, null!, "")) : throw new NotSupportedException(method.Name));
        var evidence = SourceEvidence(settings, chain, transport);

        await evidence.CaptureKnownAsync(route, [point]);
        var restarted = SourceEvidence(settings, chain, transport);
        await restarted.CaptureAsync(route, CancellationToken.None);

        var funding = await restarted.ReadAsync(route);
        Assert.Equal(point.Hash.ToString(), Assert.Single(funding!.Outputs).TransactionId);
        Assert.Equal(1025, funding.Outputs[0].AmountSats);
        Assert.False(route.SettlementVerified);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CaptureKnownAsync(route, [point, point]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CaptureKnownAsync(route, [new OutPoint(point.Hash, 1)]));
    }

    [Fact]
    public async Task PaymentSourceRecoveryFindsSpentOnchainFundingFromTheProtectedSolverCheckpoint()
    {
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var route = Ready("BTC-CHAIN", destination.ToString());
        FundAndComplete(route);
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new OutPoint(uint256.One, 0));
        transaction.Outputs.Add(Money.Satoshis(1025), destination);
        var settings = new Settings();
        var privateStore = new ArkCompositionPrivateStore(settings, new EphemeralDataProtectionProvider());
        await privateStore.WriteAsync(route.StoreId, route.RouteId, new
        {
            Request = System.Text.Json.JsonSerializer.Serialize(RecoveryRequest(route)),
            IngressSolver = new ArkEvmSolverSelection("explicit", "https://solver.example/", CompositionFixture.Key)
        });
        var chain = TestProxy.Create<IBitcoinBlockchain>((method, _) => method.Name switch
        {
            "GetUtxosAsync" => Task.FromResult<IReadOnlyList<BoardingUtxo>>([]),
            "GetRawTransactionAsync" => Task.FromResult<Transaction?>(transaction),
            _ => throw new NotSupportedException(method.Name)
        });
        var status = new RfqStatus<OnchainReceiveStatusProfile>
        {
            V = 1,
            Type = "rfq_status",
            RfqId = CompositionFixture.IngressRfq,
            State = RfqState.Funded,
            Profile = new OnchainReceiveStatusProfile
            {
                PaymentHash = route.PaymentHash,
                HtlcAddress = route.CustomerDestination,
                LockupAddress = route.Legs.Single(leg => leg.Kind == "Ingress").LockupAddress,
                FundingTxid = transaction.GetHash().ToString()
            }
        };
        var evidence = new ArkCompositionSourceEvidence(settings, chain, ServerTransport(), privateStore,
            SolverFactory(new SolverStatusHandler<OnchainReceiveStatusProfile>(status)));

        var funding = await evidence.ReadOrCaptureAsync(route, CancellationToken.None);
        Assert.NotNull(funding);
        var payment = Assert.Single(ArkCompositionPaymentSink.CreatePayments(
            route, Invoice(route), new Handler("BTC-CHAIN"), funding));
        Assert.Equal(transaction.GetHash() + "-0", payment.Id);
    }

    [Fact]
    public async Task PaymentSinkRecoversSpentSourceBeforeCreatingVerifiedOnchainPayment()
    {
        await using var routes = await CompositionDatabase.Create();
        await using var app = await ApplicationDatabase.Create();
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var route = Ready("BTC-CHAIN", destination.ToString());
        FundAndComplete(route);
        await routes.Repository.Add(route.StoreId, route);
        var invoice = Invoice(route);
        invoice.Currency = "BTC";
        invoice.InvoiceTime = DateTimeOffset.UtcNow;
        invoice.Metadata = new InvoiceMetadata();
        await app.AddAsync(invoice);
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new OutPoint(uint256.One, 0));
        transaction.Outputs.Add(Money.Satoshis(1025), destination);
        var settings = new Settings();
        var privateStore = new ArkCompositionPrivateStore(settings, new EphemeralDataProtectionProvider());
        await privateStore.WriteAsync(route.StoreId, route.RouteId, new
        {
            Request = System.Text.Json.JsonSerializer.Serialize(RecoveryRequest(route)),
            IngressSolver = new ArkEvmSolverSelection("explicit", "https://solver.example/", CompositionFixture.Key)
        });
        var ingress = route.Legs.Single(leg => leg.Kind == "Ingress");
        var status = new RfqStatus<OnchainReceiveStatusProfile>
        {
            V = 1,
            Type = "rfq_status",
            RfqId = ingress.RfqId,
            State = RfqState.Funded,
            Profile = new OnchainReceiveStatusProfile
            {
                PaymentHash = route.PaymentHash,
                HtlcAddress = route.CustomerDestination,
                LockupAddress = ingress.LockupAddress,
                FundingTxid = transaction.GetHash().ToString()
            }
        };
        var chain = TestProxy.Create<IBitcoinBlockchain>((method, _) => method.Name switch
        {
            "GetUtxosAsync" => Task.FromResult<IReadOnlyList<BoardingUtxo>>([]),
            "GetRawTransactionAsync" => Task.FromResult<Transaction?>(transaction),
            _ => throw new NotSupportedException(method.Name)
        });
        var source = new ArkCompositionSourceEvidence(settings, chain, ServerTransport(), privateStore,
            SolverFactory(new SolverStatusHandler<OnchainReceiveStatusProfile>(status)));
        var events = new BTCPayServer.EventAggregator(new Logs());
        var handlers = new PaymentMethodHandlerDictionary([new Handler("BTC-CHAIN")]);
        var invoices = new InvoiceRepository(app, events);
        var payments = new PaymentService(events, app, handlers, invoices);
        var sink = new ArkCompositionPaymentSink(invoices, payments, handlers, events, source,
            new ExecutionLock(), routes.Repository);

        await sink.SettleAsync(route, CancellationToken.None);

        var payment = Assert.Single((await invoices.GetInvoice(route.InvoiceId!)).GetPayments(false));
        Assert.Equal(transaction.GetHash() + "-0", payment.Id);
        Assert.Equal(PaymentStatus.Settled, payment.Status);
        Assert.NotNull(await source.ReadAsync(route));
    }

    [Theory]
    [InlineData("request-route")]
    [InlineData("request-store")]
    [InlineData("request-wallet")]
    [InlineData("request-rail")]
    [InlineData("request-base")]
    [InlineData("request-asset")]
    [InlineData("request-destination")]
    [InlineData("request-contract")]
    [InlineData("solver-identity")]
    [InlineData("solver-registry")]
    [InlineData("status-rfq")]
    [InlineData("status-hash")]
    [InlineData("status-htlc")]
    [InlineData("status-lock")]
    [InlineData("hint-missing")]
    [InlineData("hint-malformed")]
    [InlineData("transaction-hash")]
    [InlineData("transaction-script")]
    [InlineData("transaction-amount")]
    public async Task SourceRecoveryRejectsUntrustedOrMismatchedDiscoveryEvidence(string mismatch)
    {
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var route = Ready("BTC-CHAIN", destination.ToString());
        FundAndComplete(route);
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new OutPoint(uint256.One, 0));
        transaction.Outputs.Add(Money.Satoshis(mismatch == "transaction-amount" ? 1024 : 1025),
            mismatch == "transaction-script"
                ? new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest) : destination);
        var hinted = transaction.GetHash();
        var returned = transaction;
        if (mismatch == "transaction-hash")
        {
            returned = Transaction.Create(Network.RegTest);
            returned.Inputs.Add(new OutPoint(uint256.One, 1));
            returned.Outputs.Add(Money.Satoshis(1025), destination);
        }
        var request = RecoveryRequest(route);
        request = mismatch switch
        {
            "request-route" => request with { RouteId = Guid.NewGuid() },
            "request-store" => request with { StoreId = "other-store" },
            "request-wallet" => request with { WalletId = "other-wallet" },
            "request-rail" => request with { SourceRail = "BTC-LN" },
            "request-base" => request with { BaseAmountSats = 1001 },
            "request-asset" => request with { AssetId = request.AssetId.Replace("1111", "2222") },
            "request-destination" => request with { Destination = CompositionFixture.RefundAddress },
            "request-contract" => request with
            {
                Policy = request.Policy with { SwapContractAddress = CompositionFixture.RefundAddress }
            },
            _ => request
        };
        var ingress = route.Legs.Single(leg => leg.Kind == "Ingress");
        var status = new RfqStatus<OnchainReceiveStatusProfile>
        {
            V = 1,
            Type = "rfq_status",
            RfqId = mismatch == "status-rfq" ? CompositionFixture.OutgoingRfq : ingress.RfqId,
            State = RfqState.Funded,
            Profile = new OnchainReceiveStatusProfile
            {
                PaymentHash = mismatch == "status-hash" ? CompositionFixture.OutgoingRfq : route.PaymentHash,
                HtlcAddress = mismatch == "status-htlc" ? destination.ToString().Replace('b', 'c') : route.CustomerDestination,
                LockupAddress = mismatch == "status-lock" ? CompositionFixture.OutgoingQuote().LockupAddress : ingress.LockupAddress,
                FundingTxid = mismatch switch
                {
                    "hint-missing" => null,
                    "hint-malformed" => "not-a-transaction",
                    _ => hinted.ToString()
                }
            }
        };
        var settings = new Settings();
        var privateStore = new ArkCompositionPrivateStore(settings, new EphemeralDataProtectionProvider());
        await privateStore.WriteAsync(route.StoreId, route.RouteId, new
        {
            Request = System.Text.Json.JsonSerializer.Serialize(request),
            IngressSolver = mismatch == "solver-registry"
                ? new ArkEvmSolverSelection("registry", DiscoveryPubkey: CompositionFixture.Key)
                : new ArkEvmSolverSelection("explicit", "https://solver.example/",
                    mismatch == "solver-identity" ? CompositionFixture.OtherKey : CompositionFixture.Key)
        });
        var chain = TestProxy.Create<IBitcoinBlockchain>((method, _) => method.Name switch
        {
            "GetUtxosAsync" => Task.FromResult<IReadOnlyList<BoardingUtxo>>([]),
            "GetRawTransactionAsync" => Task.FromResult<Transaction?>(returned),
            _ => throw new NotSupportedException(method.Name)
        });
        var evidence = new ArkCompositionSourceEvidence(settings, chain, ServerTransport(), privateStore,
            SolverFactory(new SolverStatusHandler<OnchainReceiveStatusProfile>(status)));

        await Record.ExceptionAsync(() => evidence.CaptureAsync(route, CancellationToken.None));

        Assert.Null(await evidence.ReadAsync(route));
    }

    [Fact]
    public async Task SourceFundingSelectsTheExactPaymentWithoutLettingDustBlockSettlement()
    {
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var route = Ready("BTC-CHAIN", destination.ToString());
        var payment = Transaction.Create(Network.RegTest);
        payment.Inputs.Add(new OutPoint(uint256.One, 0));
        payment.Outputs.Add(Money.Satoshis(1025), destination);
        var dust = Transaction.Create(Network.RegTest);
        dust.Inputs.Add(new OutPoint(uint256.One, 1));
        dust.Outputs.Add(Money.Satoshis(1), destination);
        var transactions = new Dictionary<uint256, Transaction>
        {
            [payment.GetHash()] = payment,
            [dust.GetHash()] = dust
        };
        var chain = TestProxy.Create<IBitcoinBlockchain>((method, args) => method.Name switch
        {
            "GetUtxosAsync" => Task.FromResult<IReadOnlyList<BoardingUtxo>>([
                new(dust.GetHash().ToString(), 0, 1, true, 1, 1),
                new(payment.GetHash().ToString(), 0, 1025, true, 1, 1)]),
            "GetRawTransactionAsync" => Task.FromResult<Transaction?>(transactions[(uint256)args![0]!]),
            _ => throw new NotSupportedException(method.Name)
        });
        var transport = TestProxy.Create<IClientTransport>((method, _) => method.Name == "GetServerInfoAsync"
            ? Task.FromResult(new ArkServerInfo(Money.Zero, null!, [], Network.RegTest, new Sequence(0),
                new Sequence(0), null!, null!, null!, null!, "")) : throw new NotSupportedException(method.Name));
        var settings = new Settings();
        var evidence = SourceEvidence(settings, chain, transport);

        await evidence.CaptureAsync(route, CancellationToken.None);

        var output = Assert.Single((await evidence.ReadAsync(route))!.Outputs);
        Assert.Equal(payment.GetHash().ToString(), output.TransactionId);
        Assert.Equal(1025, output.AmountSats);
    }

    [Fact]
    public async Task SourceFundingFindsOneTransactionSplitBehindOneHundredLargerUnrelatedOutputs()
    {
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var route = Ready("BTC-CHAIN", destination.ToString());
        var payment = Transaction.Create(Network.RegTest);
        payment.Inputs.Add(new OutPoint(uint256.One, 0));
        payment.Outputs.Add(Money.Satoshis(600), destination);
        payment.Outputs.Add(Money.Satoshis(425), destination);
        var poison = Enumerable.Range(2, 100).Select(index =>
        {
            var transaction = Transaction.Create(Network.RegTest);
            transaction.Inputs.Add(new OutPoint(uint256.Parse(index.ToString("x64")), 0));
            transaction.Outputs.Add(Money.Satoshis(601), destination);
            return new BoardingUtxo(transaction.GetHash().ToString(), 0, 601, true, 1, 1);
        }).ToArray();
        var chain = TestProxy.Create<IBitcoinBlockchain>((method, args) => method.Name switch
        {
            "GetUtxosAsync" => Task.FromResult<IReadOnlyList<BoardingUtxo>>([
                .. poison,
                new(payment.GetHash().ToString(), 0, 600, true, 1, 1),
                new(payment.GetHash().ToString(), 1, 425, true, 1, 1)]),
            "GetRawTransactionAsync" => Task.FromResult<Transaction?>(
                (uint256)args![0]! == payment.GetHash() ? payment : null),
            _ => throw new NotSupportedException(method.Name)
        });
        var settings = new Settings();
        var evidence = SourceEvidence(settings, chain, ServerTransport());

        await evidence.CaptureAsync(route, CancellationToken.None);

        var outputs = (await evidence.ReadAsync(route))!.Outputs;
        Assert.Equal([425L, 600L], outputs.Select(output => output.AmountSats).Order().ToArray());
        Assert.All(outputs, output => Assert.Equal(payment.GetHash().ToString(), output.TransactionId));
    }

    [Fact]
    public async Task SourceFundingFailsClosedWhenAnExactSubsetNeedsMoreThanOneHundredOutpoints()
    {
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var route = Ready("BTC-CHAIN", destination.ToString());
        var outputs = Enumerable.Range(1, 101).Select(index => new BoardingUtxo(
            index.ToString("x64"), 0, index == 101 ? 25UL : 10UL, true, 1, 1)).ToArray();
        var rawTransactionReads = 0;
        var chain = TestProxy.Create<IBitcoinBlockchain>((method, _) => method.Name switch
        {
            "GetUtxosAsync" => Task.FromResult<IReadOnlyList<BoardingUtxo>>(outputs),
            "GetRawTransactionAsync" => throw new InvalidOperationException($"unexpected raw read {++rawTransactionReads}"),
            _ => throw new NotSupportedException(method.Name)
        });
        var settings = new Settings();
        var evidence = SourceEvidence(settings, chain, ServerTransport());

        await evidence.CaptureAsync(route, CancellationToken.None);

        Assert.Null(await evidence.ReadAsync(route));
        Assert.Equal(0, rawTransactionReads);
    }

    private static IClientTransport ServerTransport() => TestProxy.Create<IClientTransport>((method, _) =>
        method.Name == "GetServerInfoAsync"
            ? Task.FromResult(new ArkServerInfo(Money.Zero, null!, [], Network.RegTest, new Sequence(0),
                new Sequence(0), null!, null!, null!, null!, "", NetworkName: "regtest"))
            : throw new NotSupportedException(method.Name));

    private static ArkCompositionSourceEvidence SourceEvidence(Settings settings, IBitcoinBlockchain chain,
        IClientTransport transport) => new(settings, chain, transport,
            new ArkCompositionPrivateStore(settings, new EphemeralDataProtectionProvider()), null!);

    private static ArkCompositionSolverFactory SolverFactory(HttpMessageHandler handler)
    {
        var clients = TestProxy.Create<IHttpClientFactory>((method, _) => method.Name == "CreateClient"
            ? new HttpClient(handler, false) : throw new NotSupportedException(method.Name));
        return new ArkCompositionSolverFactory(null!, clients);
    }

    private static ArkCompositionExecutionRequest RecoveryRequest(ArkInvoiceComposition route) => new(
        route.RouteId, route.WalletId, route.PaymentMethodId, route.BaseAmountSats!.Value,
        route.AssetId, route.Destination, CompositionFixture.Settings().RoutePolicy!)
    {
        StoreId = route.StoreId
    };

    private static ArkSdkCompositionExecutionBackend Adapter(ArkInvoiceComposition route, long ingressAmount,
        out ArkadeSwapIntent outgoing)
    {
        outgoing = new ArkadeSwapIntent
        {
            Id = CompositionFixture.OutgoingRfq, WalletId = route.WalletId, PaymentHash = route.PaymentHash,
            Type = ArkadeSwapIntentType.BtcToEvm, Status = ArkadeSwapIntentStatus.Pending, CreatedAt = route.CreatedAt,
            OfferAmount = Money.Satoshis(1000), WantAmount = Money.Zero, SwapPkScript = CompositionFixture.LScript,
            SwapAddress = CompositionFixture.OutgoingQuote().LockupAddress, ToAssetId = route.AssetId,
            RefundLocktime = CompositionFixture.OutgoingQuote().RefundLocktime,
            Metadata = new()
            {
                [ArkadeSwapMetadataKeys.EvmAmount] = route.EvmAmount!, [ArkadeSwapMetadataKeys.EvmTokenAddress] = route.EvmTokenAddress!,
                [ArkadeSwapMetadataKeys.EvmClaimAddress] = route.EvmClaimAddress!, [ArkadeSwapMetadataKeys.EvmRefundAddress] = route.EvmRefundAddress!,
                [ArkadeSwapMetadataKeys.EvmTimeoutBlock] = route.EvmTimeoutBlock!, [ArkadeSwapMetadataKeys.EvmSwapContractAddress] = route.SwapContractAddress!
            }
        }.WithSolver(CompositionFixture.Key);
        var ingress = ArkCompositionIngressIsolationTests.Intent(CompositionFixture.IngressRfq, route.PaymentHash!);
        ingress.RefundLocktime = CompositionFixture.IngressQuote().RefundLocktime;
        ingress.SpentTxid = CompositionFixture.ClaimTx;
        ingress.WithSolver(CompositionFixture.Key);
        ingress.Metadata[ArkadeSwapMetadataKeys.ComposedOutgoingSwapId] = outgoing.Id;
        ingress.Metadata[ArkadeSwapMetadataKeys.ComposedPayoutPkScript] = outgoing.SwapPkScript;
        var all = new[] { outgoing, ingress };
        var intents = TestProxy.Create<IArkadeIntentStorage>((method, args) => method.Name == "GetArkadeSwapIntents"
            ? Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(all.Where(i => i.Id == (string)args![0]!).ToArray())
            : throw new NotSupportedException(method.Name));
        var vtxos = TestProxy.Create<IVtxoStorage>((method, args) =>
        {
            Assert.Equal("GetVtxos", method.Name);
            Assert.True((bool)args![3]!);
            var script = Assert.Single((IReadOnlyCollection<string>)args[0]!);
            return Task.FromResult<IReadOnlyCollection<ArkVtxo>>([new ArkVtxo(script,
                script == CompositionFixture.MScript ? CompositionFixture.MFundingTx : CompositionFixture.ClaimTx,
                0, (ulong)(script == CompositionFixture.MScript ? ingressAmount : 1000), null, null, false,
                DateTimeOffset.UtcNow, null, null)]);
        });
        return new ArkSdkCompositionExecutionBackend(intents, vtxos, null!, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    private static ArkInvoiceComposition Ready(string rail, string? customerDestination = null)
    {
        var route = CompositionFixture.Prepared(rail);
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        if (rail != "ARKADE") route.RecordIngressQuote(CompositionFixture.IngressQuote());
        route.RecordCustomerPrompt(rail == "ARKADE" ? CompositionFixture.OutgoingQuote().LockupAddress : customerDestination ?? "customer123", 1800000040);
        route.AttachInvoice(new InvoiceEntity { Id = "invoice", StoreId = "store" }, PaymentMethodId.Parse(rail), CompositionFixture.Hash);
        return route;
    }

    private static void FundAndComplete(ArkInvoiceComposition route)
    {
        if (route.PaymentMethodId != "ARKADE")
        {
            route.RecordIngressLockupFunded(CompositionFixture.MFundingTx, 1000);
            route.RecordIngressClaimedToOutgoing(CompositionFixture.ClaimTx);
        }
        route.RecordOutgoingLockupFunded(CompositionFixture.ClaimTx, 1000);
        route.RecordEvmLockProven(CompositionFixture.LockProof());
        route.RecordEvmClaimVerified(CompositionFixture.EvmClaimTx, "2000000");
    }

    private static InvoiceEntity Invoice(ArkInvoiceComposition route)
    {
        var invoice = new InvoiceEntity { Id = "invoice", StoreId = "store" };
        invoice.SetPaymentPrompts(new PaymentPromptDictionary([new PaymentPrompt
        {
            PaymentMethodId = PaymentMethodId.Parse(route.PaymentMethodId), Destination = route.CustomerDestination,
            Currency = "BTC", Divisibility = 8, Details = new JObject()
        }]));
        return invoice;
    }

    private sealed class Backend(string rail) : IArkCompositionExecutionBackend
    {
        public long FundingAmount { get; set; } = 1000;
        public int Advances { get; private set; }
        public Exception? Failure { get; set; }
        public ArkCompositionExecutionOutcome Result { get; set; } = new();
        public Task<ArkCompositionObservation> ObserveAsync(ArkInvoiceComposition route, CancellationToken cancellationToken) =>
            Task.FromResult(new ArkCompositionObservation(rail == "ARKADE" ? null : new(CompositionFixture.MFundingTx, FundingAmount),
                rail == "ARKADE" ? null : CompositionFixture.ClaimTx, new(CompositionFixture.ClaimTx, 1000)));
        public Task<ArkCompositionExecutionOutcome> AdvanceAsync(ArkInvoiceComposition route, CancellationToken cancellationToken)
        {
            Advances++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Result);
        }
    }

    private sealed class Sink(CompositionDatabase database) : IArkCompositionPaymentSink
    {
        public bool FailOnce { get; set; }
        public HashSet<Guid> Settlements { get; } = [];
        public async Task SettleAsync(ArkInvoiceComposition route, CancellationToken cancellationToken)
        {
            Assert.True((await database.Repository.Get(route.StoreId, route.RouteId))!.SettlementVerified);
            if (FailOnce) { FailOnce = false; throw new InvalidOperationException("Payment database unavailable."); }
            Settlements.Add(route.RouteId);
        }
    }

    private sealed class ExecutionLock : IArkCompositionExecutionLock
    {
        private readonly SemaphoreSlim _gate = new(1);
        private int _concurrency;
        public int MaxConcurrency { get; private set; }
        public async ValueTask<IAsyncDisposable> AcquireAsync(string scope, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            MaxConcurrency = Math.Max(MaxConcurrency, ++_concurrency);
            return new Lease(() => { _concurrency--; _gate.Release(); });
        }
        private sealed class Lease(Action release) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Handler(string rail) : IPaymentMethodHandler
    {
        public PaymentMethodId PaymentMethodId => PaymentMethodId.Parse(rail);
        public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;
        public Task ConfigurePrompt(PaymentMethodContext context) => throw new NotSupportedException();
        public Task BeforeFetchingRates(PaymentMethodContext context) => throw new NotSupportedException();
        public object ParsePaymentPromptDetails(JToken details) => details;
        public object ParsePaymentMethodConfig(JToken config) => config;
        public object ParsePaymentDetails(JToken details) => details;
    }

    private sealed class Settings : ISettingsRepository
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class =>
            Task.FromResult(_values.TryGetValue(name!, out var json) ? JsonConvert.DeserializeObject<T>(json) : null);
        public Task UpdateSetting<T>(T obj, string? name = null) where T : class
        {
            _values[name!] = JsonConvert.SerializeObject(obj);
            return Task.CompletedTask;
        }
        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    }

    private sealed class SolverStatusHandler<T>(RfqStatus<T> status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(status, RfqProtocol.Json),
                    System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class ApplicationDatabase : ApplicationDbContextFactory, IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        private ApplicationDatabase() : base(Options.Create(new DatabaseOptions
        {
            ConnectionString = "Host=localhost;Database=unused"
        }), NullLoggerFactory.Instance)
        {
        }

        internal static async Task<ApplicationDatabase> Create()
        {
            var database = new ApplicationDatabase();
            await database._connection.OpenAsync();
            await using var context = database.CreateContext();
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Invoices" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "Currency" TEXT NULL,
                    "Amount" NUMERIC NULL,
                    "StoreDataId" TEXT NULL,
                    "Created" TEXT NOT NULL,
                    "Blob" BLOB NULL,
                    "Blob2" JSONB NULL,
                    "Status" TEXT NULL,
                    "ExceptionStatus" TEXT NULL,
                    "Archived" INTEGER NOT NULL,
                    "XMin" INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE "Payments" (
                    "Id" TEXT NOT NULL,
                    "PaymentMethodId" TEXT NOT NULL,
                    "InvoiceDataId" TEXT NULL,
                    "Created" TEXT NULL,
                    "Currency" TEXT NULL,
                    "Amount" NUMERIC NULL,
                    "Blob" BLOB NULL,
                    "Blob2" JSONB NULL,
                    "Accounted" INTEGER NULL,
                    "Status" TEXT NULL,
                    PRIMARY KEY ("Id", "PaymentMethodId"),
                    FOREIGN KEY ("InvoiceDataId") REFERENCES "Invoices" ("Id") ON DELETE CASCADE
                );
                """);
            return database;
        }

        internal async Task AddAsync(InvoiceEntity invoice)
        {
            await using var context = CreateContext();
            var row = new BTCPayServer.Data.InvoiceData
            {
                Id = invoice.Id,
                StoreDataId = invoice.StoreId,
                Status = invoice.Status.ToString(),
                ExceptionStatus = "",
                Archived = false
            };
            row.SetBlob(invoice);
            context.Invoices.Add(row);
            await context.SaveChangesAsync();
        }

        public override ApplicationDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
            builder.UseSqlite(_connection);
            return new ApplicationDbContext(builder.Options);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
