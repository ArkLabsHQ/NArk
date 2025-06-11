using System.ComponentModel.DataAnnotations.Schema;
using Ark.V1;
using AsyncKeyedLock;
using Grpc.Core;
using System.Threading.Channels;
using BTCPayServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NArk;
using NArk.Wallet;
using NBitcoin;
using NBitcoin.WalletPolicies;

namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkPluginDbContext(DbContextOptions<ArkPluginDbContext> options) : DbContext(options)
{

    public DbSet<ArkWallet> Wallets { get; set; }
    public DbSet<ArkWalletContract> WalletContracts { get; set; }
    
    public DbSet<ArkStoredTransaction> Transactions { get; set; }
    public DbSet<VTXO> Vtxos { get; set; }
    
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.Ark");

    }
}

public enum StoredTransactionState
{
    Virtual,
    Mempool,
    Replaced,
    Confirmed,
    Invalidated
}
public class ArkStoredTransaction
{
    public string TransactionId { get; set; }
    public string Psbt { get; set; }
    public StoredTransactionState State { get; set; }
    
    public List<VTXO> CreatedVtxos { get; set; } = new List<VTXO>();
    public List<VTXO> SpentVtxos { get; set; } = new List<VTXO>();
}

public class VTXO
{
    public string TransactionId { get; set; }
    public int TransactionOutputIndex { get; set; }
    
    public string? SpentByTransactionId { get; set; }
    public int? SpentByTransactionIdInputIndex { get; set; }
    
    public long Amount { get; set; }
    public DateTimeOffset SeenAt { get; set; }
    public DateTimeOffset? SpentAt { get; set; }
    bool IsNote { get; set; }
    bool Preconfirmed { get; set; }
    
    
    public List<ArkWalletContract> WalletContracts { get; set; }
    
    public ArkStoredTransaction? SpentByTransaction { get; set; }
    public ArkStoredTransaction CreatedByTransaction { get; set; }
    
}


public class ArkWallet
{
    
    public string DescriptorTemplate { get; set; }
    public uint CurrentIndex { get; set; }

    public List<ArkWalletContract> Contracts { get; set; } = new List<ArkWalletContract>();


}

public class ArkWalletContract
{
    public string Script { get; set; }
    public string DescriptorTemplate { get; set; }
    public bool Active { get; set; }
    public string Type { get; set; }
    
    [Column(TypeName = "jsonb")]
    public Dictionary<string, string> ContractData { get; set; }
    
}


public class ArkService : IHostedService, IAsyncDisposable
{
    private ArkOperatorTerms _operatorTerms;
    private readonly BTCPayServerOptions _btcPayServerOptions;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private readonly AsyncKeyedLocker _asyncKeyedLocker;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly Ark.V1.ArkService.ArkServiceClient _arkClient;
    private readonly IndexerService.IndexerServiceClient _indexerClient;
    private readonly ILogger<ArkService> _logger;

    private Task _processingTask;
    private CancellationTokenSource? _cts;
    private readonly Channel<bool> _checkContractsChannel = Channel.CreateUnbounded<bool>();

    private string _subscriptionId;
    private HashSet<string> _subscribedScripts = new();
    private Task? _listeningTask;
    private CancellationTokenSource _listeningCts;
    private readonly Network _network;

    public ArkService(
        BTCPayNetworkProvider btcPayNetworkProvider,
        AsyncKeyedLocker asyncKeyedLocker,
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        Ark.V1.ArkService.ArkServiceClient arkClient,
        IndexerService.IndexerServiceClient indexerClient,
        ILogger<ArkService> logger)
    {
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _asyncKeyedLocker = asyncKeyedLocker;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _arkClient = arkClient;
        _indexerClient = indexerClient;
        _logger = logger;
        _network = _btcPayNetworkProvider.BTC.NBitcoinNetwork;


    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = ProcessingLoop(_cts.Token);
        _logger.LogInformation("ArkService started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ArkService stopping.");
        if (_processingTask == null)
            return;

        _checkContractsChannel.Writer.TryComplete();
        if(_cts is not null)
            await _cts.CancelAsync();

        await Task.WhenAny(_processingTask, Task.Delay(Timeout.Infinite, cancellationToken));
        _logger.LogInformation("ArkService stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            await StopListening();
            if( _cts.IsCancellationRequested)
                await _cts.CancelAsync();
            _cts.Dispose();
        }
    }

    public async Task DeriveNewContract(string walletId, Func<ArkWallet,Task<ArkWalletContract?>> setup,CancellationToken cancellationToken)
    {
        using var keyLocker = await _asyncKeyedLocker.LockAsync($"DeriveNewContract{walletId}", cancellationToken);
        await using var dbContext = _arkPluginDbContextFactory.CreateContext();
        var wallet = await dbContext.Wallets.FirstOrDefaultAsync(w => w.DescriptorTemplate == walletId, cancellationToken);
        if (wallet == null)
        {
           throw new InvalidOperationException($"Wallet with ID {walletId} not found.");
        }
        // int newIndex = (int)(wallet.CurrentIndex + 1);
        // wallet.CurrentIndex = newIndex;
        // var descriptor = Miniscript.Parse(wallet.DescriptorTemplate,
        //     new MiniscriptParsingSettings(_network, KeyType.Taproot));
        // var descriptor = descriptor.Derive(AddressIntent.Deposit, newIndex).Miniscript;
        //
        // var contract = new ArkWalletContract
        // {
        //     DescriptorTemplate = wallet.DescriptorTemplate,
        //     Descriptor = descriptor.ToString(true),
        //     Active = true
        // };
        var contract = await setup(wallet);
        if(contract == null)
        {
         return;
        }
       await  dbContext.WalletContracts.AddAsync(contract, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("New contract derived for wallet {WalletId}: {ContractScript}", walletId, contract.Script);

        await  this.UpdateManualSubscriptionAsync(contract.Script, contract.Active, cancellationToken);

    }
    

    public void TriggerContractsCheck()
    {
        _checkContractsChannel.Writer.TryWrite(true);
    }
    
    private async Task UpdateManualSubscriptionAsync(string contract, bool subscribe, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(contract))
        {
            _logger.LogWarning("Attempted to manually {Action} a null or empty contract.",
                subscribe ? "subscribe to" : "unsubscribe from");
            return;
        }

        using var keyLocker = await _asyncKeyedLocker.LockAsync("UpdateSubscription", cancellationToken);

        if (subscribe)
        {
            if (_subscribedScripts.Add(contract))
            {
                _logger.LogInformation("Manually subscribing to contract: {Contract}", contract);
                await SynchronizeSubscriptionWithIndexerAsync(cancellationToken);
            }
            else
            {
                _logger.LogInformation("Contract {Contract} is already in the manual subscription list.", contract);
            }
        }
        else
        {
            if (_subscribedScripts.Remove(contract))
            {
                _logger.LogInformation("Manually unsubscribing from contract: {Contract}", contract);
                await SynchronizeSubscriptionWithIndexerAsync(cancellationToken);
            }
            else
            {
                _logger.LogInformation("Contract {Contract} was not in the manual subscription list.", contract);
            }
        }
    }

    private async Task ProcessingLoop(CancellationToken cancellationToken)
    {
        await UpdateTerms(cancellationToken);
        TriggerContractsCheck(); // Initial check

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _checkContractsChannel.Reader.ReadAsync(cancellationToken);
                await UpdateSubscriptionAndListen(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ArkService processing loop. Retrying in 1 minute.");
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
    }

    private async Task UpdateSubscriptionAndListen(CancellationToken cancellationToken)
    {
        using var keyLocker = await _asyncKeyedLocker.LockAsync("UpdateSubscription", cancellationToken);

        await using var dbContext = _arkPluginDbContextFactory.CreateContext();
        var activeContracts = await dbContext.WalletContracts
            .Where(c => c.Active)
            .Select(c => c.Script)
            .ToListAsync(cancellationToken);

        var activeScripts = new HashSet<string>(activeContracts);

        if (activeScripts.SetEquals(_subscribedScripts))
        {
            _logger.LogDebug("No change in active contracts, skipping subscription update.");
            // Still check if listener is running
            if ((_listeningTask is null || _listeningTask.IsCompleted) && activeScripts.Any())
            {
                 _logger.LogInformation("Listener was not running, but there are active contracts. Starting listener.");
                 await StartListening(cancellationToken);
            }
            return;
        }

        _subscribedScripts = activeScripts;

        if (_subscribedScripts.Count == 0)
        {
            _logger.LogInformation("No active contracts. Stopping listener.");
            await StopListening();
            _subscriptionId = null;
            return;
        }

        _logger.LogInformation("Updating subscription with {Count} active contracts.", _subscribedScripts.Count);

        var req = new SubscribeForScriptsRequest { SubscriptionId = _subscriptionId };
        req.Scripts.AddRange(_subscribedScripts);

        try
        {
            var subscribeRes = await _indexerClient.SubscribeForScriptsAsync(req, cancellationToken: cancellationToken);
            _subscriptionId = subscribeRes.SubscriptionId;
            _logger.LogInformation("Successfully subscribed with ID: {SubscriptionId}", _subscriptionId);

            await StartListening(cancellationToken);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to subscribe to scripts. Will retry on next check.");
            _subscribedScripts.Clear(); // Force retry on next trigger
        }
    }

    private async Task StartListening(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_subscriptionId))
        {
            _logger.LogWarning("Cannot start listening without a subscription ID.");
            return;
        }
        if (_listeningTask is { IsCompleted: false })
        {
            _logger.LogDebug("Listener already running.");
            return;
        }

        await StopListening(); // Ensure previous listener is stopped

        _listeningCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listeningTask = ListenToStream(_subscriptionId, _listeningCts.Token);
        _logger.LogInformation("Stream listener started.");
    }

    private async Task StopListening()
    {
        if (_listeningTask == null) return;

        _logger.LogInformation("Stopping stream listener.");
        if(_listeningCts is not null && !_listeningCts.IsCancellationRequested)
            _listeningCts.Cancel();
        
        await Task.WhenAny(_listeningTask, Task.Delay(Timeout.Infinite, CancellationToken.None));
        
        if(_listeningCts is not null)
            _listeningCts.Dispose();
        _listeningTask = null;
        _listeningCts = null;
        _logger.LogInformation("Stream listener stopped.");
    }

    private async Task ListenToStream(string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Connecting to stream with subscription ID: {SubscriptionId}", subscriptionId);
            var stream = _indexerClient.GetSubscription(new GetSubscriptionRequest { SubscriptionId = subscriptionId }, cancellationToken: cancellationToken);

            await foreach (var response in stream.ResponseStream.ReadAllAsync(cancellationToken))
            {
                if (response == null) continue;
                _logger.LogDebug("Received update for {Count} scripts.", response.Scripts.Count);
                await ProcessUpdates(response, cancellationToken);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Stream was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream listener failed. It will be restarted on the next check.");
            // The main loop will handle restarting the subscription.
            // To ensure it restarts, we can trigger a check.
            TriggerContractsCheck();
        }
        finally
        {
            _logger.LogInformation("ListenToStream finished.");
        }
    }

    private Task ProcessUpdates(GetSubscriptionResponse scripts, CancellationToken cancellationToken)
    {
        // TODO: Implement logic to process the script updates.
        // For example, you might want to fetch transaction details for these scripts.
        _logger.LogInformation("Processing updates for {Count} scripts.", scripts.Scripts.Count);
        foreach (var script in scripts.Scripts)
        {
            _logger.LogDebug("Updated script: {Script}", script);
        }
        return Task.CompletedTask;
    }

    private async Task SynchronizeSubscriptionWithIndexerAsync(CancellationToken cancellationToken)
    {
        if (_subscribedScripts.Count == 0)
        {
            _logger.LogInformation("[Manual] No active scripts. Stopping listener and clearing subscription.");
            await StopListening();
            _subscriptionId = null;
            return;
        }

        _logger.LogInformation("[Manual] Updating remote subscription with {Count} scripts.", _subscribedScripts.Count);

        var req = new SubscribeForScriptsRequest { SubscriptionId = _subscriptionId }; 
        req.Scripts.AddRange(_subscribedScripts);

        try
        {
            var subscribeRes = await _indexerClient.SubscribeForScriptsAsync(req, cancellationToken: cancellationToken);
            var newSubscriptionId = subscribeRes.SubscriptionId;

            if (_subscriptionId != newSubscriptionId && !string.IsNullOrEmpty(_subscriptionId))
            {
                _logger.LogWarning("Subscription ID changed from {OldSubscriptionId} to {NewSubscriptionId} during manual update. Listener will be restarted.", _subscriptionId, newSubscriptionId);
                await StopListening(); 
            }
            _subscriptionId = newSubscriptionId;
            _logger.LogInformation("[Manual] Successfully updated subscription with ID: {SubscriptionId}", _subscriptionId);

            await StartListening(cancellationToken);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "[Manual] Failed to update remote subscription.");
        }
    }
    
    private async Task UpdateTerms(CancellationToken cancellationToken)
    {
        try
        {
            var info = await _arkClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
            var terms = info.ArkOperatorTerms();
            _operatorTerms = terms;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update operator terms.");
        }
    }
}
