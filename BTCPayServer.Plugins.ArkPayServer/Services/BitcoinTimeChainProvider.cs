using BTCPayServer.HostedServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class BitcoinTimeChainProvider : EventHostedServiceBase
{
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly IMemoryCache _cache;
    private const string CacheKey = "BitcoinTimeChainProvider";

    public BitcoinTimeChainProvider(ExplorerClientProvider explorerClientProvider, IMemoryCache cache,
        ILogger<BitcoinTimeChainProvider> logger, EventAggregator aggregator) : base(aggregator, logger)
    {
        _explorerClientProvider = explorerClientProvider;
        _cache = cache;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<Events.NewBlockEvent>();
        base.SubscribeToEvents();
    }

    protected override Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is Events.NewBlockEvent)
        {
            _cache.Remove(CacheKey);
        }

        return base.ProcessEvent(evt, cancellationToken);
    }

    public async Task<(long Timestamp, uint Height)> Get(CancellationToken cancellationToken)
    {
        return await _cache.GetOrCreateAsync<(long Timestamp, uint Height)>(CacheKey, async entry =>
        {
            var client = _explorerClientProvider.GetExplorerClient("BTC");
            var result = await client.RPCClient.SendCommandAsync("getblockchaininfo", cancellationToken)
                .ConfigureAwait(false);
            var info = JsonConvert.DeserializeObject<GetBlockchainInfoResponse>(result.ResultString);
            return (info.MedianTime, info.Blocks);
        });
    }

    public class GetBlockchainInfoResponse
    {
        [JsonProperty("blocks")] public uint Blocks { get; set; }

        [JsonProperty("mediantime")] public long MedianTime { get; set; }
    }
}