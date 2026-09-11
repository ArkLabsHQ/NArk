using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;
using NArk.ArkadeIntents.Rfq.Profiles.Lightning;
using NArk.ArkadeIntents.SolverRegistry;
using NBitcoin;
using Xunit;

namespace NArk.Tests;

public class ArkSdkCompositionExecutorTests
{
    private const string PreparedEvmTransaction = "0x02f86c01843b9aca008252089400000000000000000000000000000000deadbeef80b844claim-with-secret";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OutgoingId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string IngressId = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string L = "5120dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string Pubkey = "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    private const string Asset = "eip155:31337/erc20:0x1111111111111111111111111111111111111111";

    [Fact]
    public async Task PreparationIsCiphertextAndBoundToStoreAndRoute()
    {
        var settings = new Settings();
        var provider = new EphemeralDataProtectionProvider();
        var storage = new ArkCompositionPrivateStore(settings, provider);
        var route = Guid.NewGuid();
        await storage.WriteAsync("store", route, new SecretState(Hash));
        Assert.DoesNotContain(Hash, Assert.IsType<ArkCompositionPrivateStore.Envelope>(settings.Value).Ciphertext);
        var restarted = new ArkCompositionPrivateStore(settings, provider);
        Assert.Equal(Hash, (await restarted.ReadAsync<SecretState>("store", route))!.Preimage);
        await Assert.ThrowsAsync<CryptographicException>(() => restarted.ReadAsync<SecretState>("other-store", route));
        var otherRoute = Guid.NewGuid();
        settings.Values[$"ArkCompositionRecovery-{otherRoute:N}"] = settings.Value!;
        await Assert.ThrowsAsync<CryptographicException>(() => restarted.ReadAsync<SecretState>("store", otherRoute));
        await storage.WriteAsync("store", otherRoute, new SecretState(IngressId));
        Assert.Equal(Hash, (await restarted.ReadAsync<SecretState>("store", route))!.Preimage);
        Assert.Equal(IngressId, (await restarted.ReadAsync<SecretState>("store", otherRoute))!.Preimage);
    }

    [Fact]
    public async Task ProductionRpcClientSendsCredentialsWithoutLoggingThem()
    {
        var logs = new CapturedLogs();
        var handler = new RpcHandler();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddArkCompositionSdk();
        services.AddHttpClient("ArkCompositionEvm").ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var endpoint = new Uri("https://rpc.example/private-rpc-token?apiKey=private-query-token");
        var rpc = new EvmJsonRpcClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("ArkCompositionEvm"), endpoint);
        Assert.Equal(31337, await rpc.GetChainIdAsync());
        Assert.Equal(endpoint, handler.Endpoint);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("private-rpc-token") || message.Contains("private-query-token"));
    }

    [Fact]
    public async Task FundedRouteRetainsOriginalPolicyAfterMerchantChangesOrDisablesIt()
    {
        var protection = new EphemeralDataProtectionProvider();
        var endpoints = new ArkEvmRpcEndpointProtector(protection);
        var keys = new ArkEvmGasPayerProtector(protection);
        const string sender = "0xf39fd6e51aad88f6f4ce6ab8827279cfffb92266";
        const string key = "ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
        var original = CompositionFixture.Settings();
        var current = original with
        {
            Enabled = false, AssetId = Asset, Destination = "0x3333333333333333333333333333333333333333",
            ProtectedRpcUri = endpoints.Protect("store", "https://rpc.example/?key=protected-token"),
            ProtectedGasPayerPrivateKey = keys.Protect("store", key, sender), ExpectedSenderAddress = sender,
            MaxFeePerGasWei = "100000000000", MaxPriorityFeePerGasWei = "2000000000", MaxGasLimit = "500000"
        };
        var services = new ServiceCollection();
        services.AddArkCompositionSdk();
        using var provider = services.BuildServiceProvider();
        var factory = new ArkCompositionEvmContextFactory(new ContextSource(ArkCompositionPromptTests.Store(current)),
            endpoints, keys, provider.GetRequiredService<IHttpClientFactory>());
        var request = new ArkCompositionExecutionRequest(Guid.NewGuid(), "private-wallet", "ARKADE", 1000,
            original.AssetId, original.Destination, original.RoutePolicy!) { StoreId = "store" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.OpenAsync(request));
        using var recovered = await factory.OpenForRecoveryAsync(request);
        Assert.Equal(ArkCompositionEvmContextFactory.Policy(original.AssetId, original.RoutePolicy!).ChainId, recovered.Policy.ChainId);
        Assert.Equal(original.RoutePolicy!.SwapContractAddress, recovered.Policy.SwapContractAddress);
        Assert.Equal(sender, recovered.Sender.Address);
    }

    [Fact]
    public async Task ProductionPreparationIsIndependentPerRailAndRepeatableAfterRestart()
    {
        var protection = new EphemeralDataProtectionProvider();
        var endpoints = new ArkEvmRpcEndpointProtector(protection);
        var keys = new ArkEvmGasPayerProtector(protection);
        const string sender = "0xf39fd6e51aad88f6f4ce6ab8827279cfffb92266";
        var configured = CompositionFixture.Settings() with
        {
            ProtectedRpcUri = endpoints.Protect("store", "https://rpc.example/?key=protected-token"),
            ProtectedGasPayerPrivateKey = keys.Protect("store", "ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80", sender),
            ExpectedSenderAddress = sender, MaxFeePerGasWei = "100000000000", MaxPriorityFeePerGasWei = "2000000000", MaxGasLimit = "500000"
        };
        var services = new ServiceCollection();
        services.AddArkCompositionSdk();
        using var provider = services.BuildServiceProvider();
        var contexts = new ArkCompositionEvmContextFactory(new ContextSource(ArkCompositionPromptTests.Store(configured)),
            endpoints, keys, provider.GetRequiredService<IHttpClientFactory>());
        var settings = new Settings();
        ArkSdkCompositionExecutor Executor() => new(new ArkCompositionPrivateStore(settings, protection), contexts,
            null!, null!, null!, null!, provider, null!, Options.Create(new ArkadeIntentsOptions()));
        var preparations = new List<ArkCompositionPreparation>();
        foreach (var rail in new[] { "ARKADE", "BTC-LN", "BTC-CHAIN" })
        {
            var request = new ArkCompositionExecutionRequest(Guid.NewGuid(), "private-wallet", rail, 10000,
                configured.AssetId, configured.Destination, configured.RoutePolicy!) { StoreId = "store" };
            var preparation = await Executor().PrepareAsync(request, CancellationToken.None);
            preparations.Add(preparation);
            Assert.Equal(preparation, await Executor().PrepareAsync(request, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Executor().PrepareAsync(request with { BaseAmountSats = 10001 }, CancellationToken.None));
        }
        Assert.Equal(3, preparations.Select(p => p.PaymentHash).Distinct().Count());
        Assert.Equal(5, preparations.SelectMany(p => new[] { p.OutgoingRfqId, p.IngressRfqId }).Where(id => id is not null).Distinct().Count());
        Assert.All(settings.Values.Values, value => Assert.IsType<ArkCompositionPrivateStore.Envelope>(value));
    }

    [Fact]
    public async Task CrashAfterSdkSaveReplaysCheckpointWithoutRequotingOrResettingProgress()
    {
        var settings = new Settings();
        var protection = new EphemeralDataProtectionProvider();
        var privateStore = new ArkCompositionPrivateStore(settings, protection);
        var routeId = Guid.NewGuid();
        var remote = new QuoteTransport();
        var state = new ArkCompositionRfqCheckpointState();
        var checkpoint = new ArkCompositionRfqCheckpoint(remote, state,
            _ => privateStore.WriteAsync("store", routeId, state));
        var request = EvmSendProfile.Request(10000, Hash, "0x2222222222222222222222222222222222222222",
            "ark-refund", Pubkey, "0x1111111111111111111111111111111111111111", OutgoingId);
        var quote = await checkpoint.RequestEvmSendQuoteAsync(request);
        var inner = new IntentStorage();
        using var protectedIntents = new ArkProtectedIntentStorage(inner, protection);
        var quoteStorage = new ArkCompositionQuoteIntentStorage(protectedIntents);
        await quoteStorage.SaveArkadeSwapIntent(Intent());
        settings.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(() => privateStore.WriteAsync("store", routeId, new SecretState("public-result")));
        inner.Value!.Status = ArkadeSwapIntentStatus.Fulfilled;
        var restored = (await privateStore.ReadAsync<ArkCompositionRfqCheckpointState>("store", routeId))!;
        var restarted = new ArkCompositionRfqCheckpoint(remote, restored,
            _ => privateStore.WriteAsync("store", routeId, restored));
        var replayed = await restarted.RequestEvmSendQuoteAsync(request);
        await quoteStorage.SaveArkadeSwapIntent(Intent());
        Assert.Equal(quote.RfqId, replayed.RfqId);
        Assert.Equal(1, remote.Calls);
        Assert.Equal(ArkadeSwapIntentStatus.Fulfilled, inner.Value.Status);
        Assert.StartsWith("protected:v1:", inner.Value.Metadata[ArkadeSwapMetadataKeys.Preimage]);
    }

    [Fact]
    public async Task LostIngressReplyRetriesTheOriginalOrdinaryRequestIncludingSealedPacket()
    {
        var privateStore = new ArkCompositionPrivateStore(new Settings(), new EphemeralDataProtectionProvider());
        var route = Guid.NewGuid();
        var state = new ArkCompositionRfqCheckpointState();
        var remote = new QuoteTransport { LoseFirstReply = true };
        var original = LightningReceiveProfile.Request(10000, RfqAmountSide.To, Hash, "outgoing-L", Pubkey, "original-sealed-packet", IngressId);
        var checkpoint = new ArkCompositionRfqCheckpoint(remote, state, _ => privateStore.WriteAsync("store", route, state));
        await Assert.ThrowsAsync<IOException>(() => checkpoint.RequestQuoteAsync<LightningReceiveRequestProfile, LightningReceiveQuoteProfile>(original));
        var restored = (await privateStore.ReadAsync<ArkCompositionRfqCheckpointState>("store", route))!;
        var regenerated = LightningReceiveProfile.Request(10000, RfqAmountSide.To, Hash, "outgoing-L", Pubkey, "new-sealed-packet", IngressId);
        var restarted = new ArkCompositionRfqCheckpoint(remote, restored, _ => privateStore.WriteAsync("store", route, restored));
        await restarted.RequestQuoteAsync<LightningReceiveRequestProfile, LightningReceiveQuoteProfile>(regenerated);
        Assert.Equal(2, remote.Calls);
        Assert.Equal(remote.Requests[0], remote.Requests[1]);
        Assert.Contains("original-sealed-packet", remote.Requests[1]);
        Assert.DoesNotContain("new-sealed-packet", remote.Requests[1]);
    }

    [Fact]
    public async Task IntentProtectionPreservesCallerEventsAndRestartRecovery()
    {
        var inner = new IntentStorage();
        var provider = new EphemeralDataProtectionProvider();
        var intent = Intent();
        using (var storage = new ArkProtectedIntentStorage(inner, provider))
        {
            ArkadeSwapIntent? observed = null;
            storage.SwapsChanged += (_, value) => observed = value;
            await storage.SaveArkadeSwapIntent(intent);
            Assert.Equal(Hash, intent.Metadata[ArkadeSwapMetadataKeys.Preimage]);
            Assert.Equal(PreparedEvmTransaction,
                intent.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction]);
            Assert.Equal(Hash, observed!.Metadata[ArkadeSwapMetadataKeys.Preimage]);
            Assert.Equal(PreparedEvmTransaction,
                observed.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction]);
            Assert.StartsWith("protected:v1:", inner.Value!.Metadata[ArkadeSwapMetadataKeys.Preimage]);
            Assert.DoesNotContain(Hash, inner.Value.Metadata[ArkadeSwapMetadataKeys.Preimage]);
            Assert.StartsWith("protected:v1:",
                inner.Value.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction]);
            Assert.DoesNotContain(PreparedEvmTransaction,
                inner.Value.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction]);
        }
        using var restarted = new ArkProtectedIntentStorage(inner, provider);
        var recovered = await restarted.GetArkadeSwapIntent(OutgoingId);
        Assert.Equal(Hash, recovered!.Metadata[ArkadeSwapMetadataKeys.Preimage]);
        Assert.Equal(PreparedEvmTransaction,
            recovered.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction]);
        inner.Value!.WalletId = "another-wallet";
        await Assert.ThrowsAsync<CryptographicException>(() => restarted.GetArkadeSwapIntent(OutgoingId));
    }

    [Fact]
    public async Task ExistingPlaintextIntentsRemainReadableAndBecomeProtectedWhenSaved()
    {
        var inner = new IntentStorage { Value = Intent() };
        using var storage = new ArkProtectedIntentStorage(inner, new EphemeralDataProtectionProvider());
        var recovered = await storage.GetArkadeSwapIntent(OutgoingId);
        Assert.Equal(Hash, recovered!.Metadata[ArkadeSwapMetadataKeys.Preimage]);
        Assert.Equal(PreparedEvmTransaction,
            recovered.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction]);
        await storage.SaveArkadeSwapIntent(recovered);
        Assert.StartsWith("protected:v1:", inner.Value!.Metadata[ArkadeSwapMetadataKeys.Preimage]);
        Assert.StartsWith("protected:v1:",
            inner.Value.Metadata[ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction]);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("payout")]
    [InlineData("amount")]
    [InlineData("rfq")]
    [InlineData("fee")]
    public void IngressMustReuseHashAndFundExactIndependentOutgoingLock(string mismatch)
    {
        var request = new ArkCompositionExecutionRequest(Guid.NewGuid(), "wallet", "BTC-LN", 10_000,
            Asset, "0x2222222222222222222222222222222222222222", new ArkEvmRoutePolicy());
        var preparation = new ArkCompositionPreparation(Hash, OutgoingId, IngressId);
        var outgoing = new ArkCompositionQuote(OutgoingId, Hash, Pubkey, "10000", "99000", L, "ark-address", 200, 500);
        var ingress = new ArkCompositionQuote(IngressId, Hash, Pubkey, "10005", "10000", "M", "ingress-address", 190, 600, L);
        ArkSdkCompositionExecutor.ValidateIngress(request, preparation, outgoing, ingress);
        var invalid = mismatch switch
        {
            "hash" => ingress with { PaymentHash = IngressId },
            "payout" => ingress with { PayoutScript = "wallet-change" },
            "amount" => ingress with { ToAmount = "9999" },
            "rfq" => ingress with { RfqId = OutgoingId },
            _ => ingress with { FromAmount = "9999" }
        };
        Assert.Throws<InvalidOperationException>(() => ArkSdkCompositionExecutor.ValidateIngress(request, preparation, outgoing, invalid));
    }

    [Fact]
    public void RegistrySelectionPinsCanonicalChainTokenRailIdentityAndSize()
    {
        var expected = Market(Asset);
        var wrongChain = Market(Asset.Replace("31337", "1"));
        var wrongToken = Market(Asset.Replace("1111111111", "3333333333"));
        var selection = new ArkEvmSolverSelection("registry", DiscoveryPubkey: Pubkey);
        Assert.Same(expected, ArkCompositionSolverFactory.Select([wrongChain, wrongToken, expected], selection,
            "regtest", Asset, "ARKADE", 10000));
        Assert.Null(ArkCompositionSolverFactory.Select([wrongChain, wrongToken], selection, "regtest", Asset, "ARKADE", 10000));
        Assert.Null(ArkCompositionSolverFactory.Select([expected], selection, "regtest", Asset, "BTC-LN", 10000));
        Assert.Null(ArkCompositionSolverFactory.Select([expected], selection, "regtest", Asset, "ARKADE", 999999));
    }

    private static IndexedMarket Market(string quote) => new()
    {
        Solver = "solver", DiscoveryPubkey = Pubkey,
        BaseAsset = new AssetDescriptor { Id = "arkade:regtest/slip44:1" }, QuoteAsset = new AssetDescriptor { Id = quote },
        MinBaseAmount = 1000, MaxBaseAmount = 100000,
        Transports = new SolverTransports { Nostr = new NostrTransport { Relays = ["wss://relay.example/"] } }
    };

    private static ArkadeSwapIntent Intent() => new()
    {
        Id = OutgoingId, WalletId = "wallet", Type = ArkadeSwapIntentType.BtcToEvm,
        OfferAmount = Money.Satoshis(10000), WantAmount = Money.Zero, Status = ArkadeSwapIntentStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow, SwapPkScript = L, SwapAddress = "ark-address",
        Metadata = new Dictionary<string, string>
        {
            [ArkadeSwapMetadataKeys.Preimage] = Hash,
            [ArkadeSwapMetadataKeys.EvmClaimPreparedTransaction] = PreparedEvmTransaction
        }
    };

    public sealed record SecretState(string Preimage);
    private sealed class ContextSource(StoreData store) : IArkCompositionContextSource
    {
        public Task<StoreData?> FindStoreAsync(string storeId) => Task.FromResult<StoreData?>(storeId == store.Id ? store : null);
        public Task<InvoiceEntity?> FindInvoiceAsync(string invoiceId) => Task.FromResult<InvoiceEntity?>(null);
    }
    private sealed class Settings : ISettingsRepository
    {
        public Dictionary<string, object> Values { get; } = new();
        public object? Value { get; private set; }
        public bool FailNextWrite { get; set; }
        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class => Task.FromResult(Values.GetValueOrDefault(name!) as T);
        public Task UpdateSetting<T>(T obj, string? name = null) where T : class
        {
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("Injected checkpoint failure."); }
            Value = obj;
            Values[name!] = obj;
            return Task.CompletedTask;
        }
        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    }

    private sealed class RpcHandler : HttpMessageHandler
    {
        public Uri? Endpoint { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"0x7a69\"}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { lock (messages) messages.Add(formatter(state, exception)); }
        }
    }

    private sealed class QuoteTransport : IRfqTransport
    {
        public int Calls { get; private set; }
        public bool LoseFirstReply { get; init; }
        public List<string> Requests { get; } = [];
        public Task<RfqQuote<EvmSendQuoteProfile>> RequestEvmSendQuoteAsync(EvmSendRfqRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new RfqQuote<EvmSendQuoteProfile>
            {
                V = 1, Type = "rfq_quote", RfqId = request.RfqId, Pair = request.Pair, SolverPubkey = Pubkey,
                FromAmount = 10000, ToAmount = 90000, ValidUntil = 200, RefundLocktime = 500
            });
        }
        public Task<RfqQuote<TQuoteProfile>> RequestQuoteAsync<TRequestProfile, TQuoteProfile>(RfqRequest<TRequestProfile> request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(JsonSerializer.Serialize(request, RfqProtocol.Json));
            if (LoseFirstReply && Calls == 1) throw new IOException("Injected lost RFQ response.");
            return Task.FromResult(new RfqQuote<TQuoteProfile>
            {
                V = 1, Type = "rfq_quote", RfqId = request.RfqId, Pair = request.Pair, SolverPubkey = Pubkey,
                FromAmount = 10005, ToAmount = 10000, ValidUntil = 190, RefundLocktime = 600
            });
        }
        public Task<RfqStatus<TStatusProfile>?> GetStatusAsync<TStatusProfile>(string rfqId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class IntentStorage : IArkadeIntentStorage
    {
        public ArkadeSwapIntent? Value { get; set; }
        public event EventHandler<ArkadeSwapIntent>? SwapsChanged;
        public event EventHandler? ActiveScriptsChanged;
        public Task<IReadOnlyCollection<ArkadeSwapIntent>> GetArkadeSwapIntents(string? id = null,
            ArkadeSwapIntentStatus? status = null, ArkadeSwapIntentStatus[]? statuses = null, string? swapPkScript = null,
            string[]? walletIds = null, int? skip = null, int? take = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ArkadeSwapIntent>>(Value is null ? [] : [Value]);
        public Task SaveArkadeSwapIntent(ArkadeSwapIntent intent, CancellationToken cancellationToken = default)
        {
            Value = intent;
            SwapsChanged?.Invoke(this, intent);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task<bool> UpdateStatus(string swapPkScript, ArkadeSwapIntentStatus status, string? spentTxid = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
