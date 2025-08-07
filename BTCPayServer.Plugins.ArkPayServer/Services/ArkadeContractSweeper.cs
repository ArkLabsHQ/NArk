using AsyncKeyedLock;
using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Services;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeContractSweeper : IHostedService
{
    private readonly ArkadeSpender _arkadeSpender;
    private readonly ArkWalletService _arkWalletService;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<ArkadeContractSweeper> _logger;
    private readonly IOperatorTermsService _operatorTermsService;
    private readonly ArkSubscriptionService _arkSubscriptionService;
    private CompositeDisposable _leases = new();
    private CancellationTokenSource _cts = new();
    private TaskCompletionSource? _tcsWaitForNextPoll;
    

    public ArkadeContractSweeper(
        ArkadeSpender arkadeSpender,
        ArkWalletService arkWalletService,
        EventAggregator eventAggregator,
        ILogger<ArkadeContractSweeper> logger,
        IOperatorTermsService operatorTermsService,
        ArkSubscriptionService arkSubscriptionService)
    {
        _arkadeSpender = arkadeSpender;
        _arkWalletService = arkWalletService;
        _eventAggregator = eventAggregator;
        _logger = logger;
        _operatorTermsService = operatorTermsService;
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
        while (!_cts.IsCancellationRequested)
        {
            _logger.LogInformation("Polling for vtxos to sweep.");
            try
            {
                var spendableCoinsByWallet = await _arkadeSpender.GetSpendableCoins(null, _cts.Token);
                var wallets = await _arkWalletService.GetWallets(spendableCoinsByWallet.Keys.ToArray(), _cts.Token);
                var terms = await _operatorTermsService.GetOperatorTerms(_cts.Token);
                foreach (var group in spendableCoinsByWallet)
                {
                    try
                    {
                        var wallet = wallets.First(x => x.Id == group.Key);
                        var destination = await _arkadeSpender.GetDestination(wallet, terms);
        
                        // Only sweep if we have coins not at the destination to avoid infinite sweeping loops
                        if (group.Value.All(x => x.TxOut.IsTo(destination)))
                        {
                            _logger.LogInformation($"Skipping sweep for wallet {wallet.Id}: all coins are already at destination");
                           continue;
                        }
                        
                        if(group.Value.Count == 0)
                        {
                            _logger.LogInformation($"Skipping sweep for wallet {wallet.Id}: no coins to sweep");
                            continue;
                        }
                        await _arkadeSpender.Spend(wallet, group.Value, [], _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error while sweeping vtxos for wallet {group.Key}");
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