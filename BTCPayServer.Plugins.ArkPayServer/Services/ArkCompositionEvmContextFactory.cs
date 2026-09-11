using System.Globalization;
using System.Numerics;
using System.Text.Json;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.SolverRegistry;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkCompositionEvmContextFactory(IArkCompositionContextSource source,
    ArkEvmRpcEndpointProtector endpoints, ArkEvmGasPayerProtector keys, IHttpClientFactory http)
{
    public Task<ArkCompositionEvmContext> OpenAsync(ArkCompositionExecutionRequest request,
        CancellationToken cancellationToken = default) => OpenCoreAsync(request, false, cancellationToken);

    public Task<ArkCompositionEvmContext> OpenForRecoveryAsync(ArkCompositionExecutionRequest request,
        CancellationToken cancellationToken = default) => OpenCoreAsync(request, true, cancellationToken);

    private async Task<ArkCompositionEvmContext> OpenCoreAsync(ArkCompositionExecutionRequest request,
        bool recovery, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = await source.FindStoreAsync(request.StoreId)
            ?? throw new InvalidOperationException("The composition store is unavailable.");
        var configuration = ArkCompositionPromptService.Configuration(store);
        var settings = configuration?.EvmSettlement?.Validate();
        if (settings is null || !recovery && (configuration?.WalletId != request.WalletId || settings is not { Enabled: true, RoutePolicy: not null }
            || settings.AssetId != request.AssetId || settings.Destination != request.Destination
            || JsonSerializer.Serialize(settings.RoutePolicy) != JsonSerializer.Serialize(request.Policy)
            || !settings.RoutePolicy.EnabledSourceRails.Contains(request.SourceRail)))
            throw new InvalidOperationException("The route differs from the store's settlement configuration.");
        var endpoint = endpoints.TryUnprotect(request.StoreId, settings.ProtectedRpcUri)
            ?? throw new InvalidOperationException("The store's EVM RPC configuration is unavailable.");
        if (!keys.IsAvailable(request.StoreId, settings.ProtectedGasPayerPrivateKey, settings.ExpectedSenderAddress)
            || settings.MaxFeePerGasWei is null || settings.MaxPriorityFeePerGasWei is null || settings.MaxGasLimit is null)
            throw new InvalidOperationException("The store's EVM gas-payer configuration is unavailable.");
        var client = http.CreateClient("ArkCompositionEvm");
        try
        {
            var rpc = new EvmJsonRpcClient(client, endpoint);
            var sender = keys.UseKey(request.StoreId, settings.ProtectedGasPayerPrivateKey!, settings.ExpectedSenderAddress,
                key => new EvmLocalTransactionSender(rpc, key.Span, new EvmTransactionSenderOptions
                {
                    ExpectedSenderAddress = settings.ExpectedSenderAddress!,
                    MaxFeePerGasWei = BigInteger.Parse(settings.MaxFeePerGasWei, CultureInfo.InvariantCulture),
                    MaxPriorityFeePerGasWei = BigInteger.Parse(settings.MaxPriorityFeePerGasWei, CultureInfo.InvariantCulture),
                    MaxGasLimit = BigInteger.Parse(settings.MaxGasLimit, CultureInfo.InvariantCulture)
                }));
            return new ArkCompositionEvmContext(client, rpc, sender, Policy(request.AssetId, request.Policy));
        }
        catch { client.Dispose(); throw; }
    }

    public static EvmSendPolicy Policy(string assetId, ArkEvmRoutePolicy policy)
    {
        var asset = AssetIdentifier.Parse(assetId);
        if (asset.Namespace != "eip155" || !asset.Asset.StartsWith("erc20:", StringComparison.Ordinal))
            throw new ArgumentException("An exact ERC20 asset is required.");
        policy = policy.Validate();
        return new EvmSendPolicy
        {
            ChainId = BigInteger.Parse(asset.ChainReference, CultureInfo.InvariantCulture),
            TokenAddress = asset.Asset["erc20:".Length..], SwapContractAddress = policy.SwapContractAddress,
            FastestSecondsPerBlock = policy.FastestSecondsPerBlock, SlowestSecondsPerBlock = policy.SlowestSecondsPerBlock,
            MinConfirmations = policy.MinConfirmations, MinAgeSeconds = policy.MinAgeSeconds,
            MinimumClaimWindowSeconds = policy.MinimumClaimWindowSeconds,
            ArkadeRefundMarginSeconds = policy.ArkadeRefundMarginSeconds,
            RequireEmulatorRefundPath = policy.RequireEmulatorRefundPath
        };
    }
}

public sealed class ArkCompositionEvmContext(HttpClient http, EvmJsonRpcClient rpc,
    EvmLocalTransactionSender sender, EvmSendPolicy policy) : IDisposable
{
    public EvmJsonRpcClient Rpc { get; } = rpc;
    public EvmLocalTransactionSender Sender { get; } = sender;
    public EvmSendPolicy Policy { get; } = policy;
    public void Dispose() => http.Dispose();
    public override string ToString() => nameof(ArkCompositionEvmContext);
}
