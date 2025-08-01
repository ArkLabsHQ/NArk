using AsyncKeyedLock;
using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeContractSweeper : IHostedService
{
    private readonly AsyncKeyedLocker _asyncKeyedLocker;
    private readonly ArkadeSpender _arkadeSpender;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly ArkadeWalletSignerProvider _walletSignerProvider;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<ArkadeContractSweeper> _logger;
    private readonly ArkSubscriptionService _arkSubscriptionService;
    private CompositeDisposable _leases = new();
    private CancellationTokenSource _cts = new();
    private TaskCompletionSource? _tcsWaitForNextPoll;
    

    public ArkadeContractSweeper(
        AsyncKeyedLocker asyncKeyedLocker,
        ArkadeSpender arkadeSpender,
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        ArkadeWalletSignerProvider walletSignerProvider,
        EventAggregator eventAggregator,
        ILogger<ArkadeContractSweeper> logger,
        ArkSubscriptionService arkSubscriptionService)
    {
        _asyncKeyedLocker = asyncKeyedLocker;
        _arkadeSpender = arkadeSpender;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _walletSignerProvider = walletSignerProvider;
        _eventAggregator = eventAggregator;
        _logger = logger;
        _arkSubscriptionService = arkSubscriptionService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _leases.Add(_eventAggregator.SubscribeAsync<VTXOsUpdated>(OnVTXOsUpdated));
        _ = PollForVTXOToSweep();
        return Task.CompletedTask;
    }

    private async Task PollForVTXOToSweep()
    {
        await _arkSubscriptionService.StartedTask.WithCancellation(_cts.Token);
        // string[] allowedContractTypes = {VHTLCContract.ContractType, HashLockedArkPaymentContract.ContractType, ArkPaymentContract.ContractType};

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var db = _arkPluginDbContextFactory.CreateContext();

                var vtxosAndContracts = await db.Vtxos
                    .Where(vtxo => vtxo.SpentByTransactionId == null && !vtxo.IsNote) // VTXO is unspent
                    .Join(
                        db.WalletContracts,
                        vtxo => vtxo.Script, // Key from VTXO
                        contract => contract.Script, // Key from WalletContract
                        (vtxo, contract) => new {Vtxo = vtxo, Contract = contract} // Select both VTXO and contract
                    )
                    .ToListAsync(_cts.Token);
                    
                var groupedByWallet = vtxosAndContracts.GroupBy(x => x.Contract.WalletId).ToList();

                var walletsToCheck = groupedByWallet.Select(g => g.Key);
                var walletSigners = await _walletSignerProvider.GetSigners(walletsToCheck.ToArray(), _cts.Token);

                foreach (var group in groupedByWallet)
                {
                    var signer = walletSigners.TryGet(group.Key);
                    if (signer is null)
                        continue;

                    var wallet = await db.Wallets.FirstOrDefaultAsync(x => x.Id == group.Key, _cts.Token);
                    if (wallet is null)
                        continue;

                    try
                    {
                        using var walletLock = await _asyncKeyedLocker.LockAsync($"ark-{wallet.Id}-txs-spending", _cts.Token);
                        
                        // Use the pre-fetched VTXOs - they're safe to use since we hold the wallet lock
                        var walletVtxos = group.Select(x => (x.Vtxo, x.Contract)).ToArray();
                        
                        if (walletVtxos.Length > 0)
                        {
                            await _arkadeSpender.SweepWalletWithLock(wallet, walletVtxos, signer, _cts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error while sweeping vtxos for wallet {wallet.Id}");
                    }
                }
                
                _tcsWaitForNextPoll = new TaskCompletionSource();
                using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await _tcsWaitForNextPoll.Task.WithCancellation(CancellationTokenSource
                    .CreateLinkedTokenSource(_cts.Token, cts2.Token).Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while sweeping vtxos");
                await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token);
            }
        }
    }

    private Task OnVTXOsUpdated(VTXOsUpdated arg)
    {
        _tcsWaitForNextPoll?.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        _leases.Dispose();
        _leases = new CompositeDisposable();
    }
}