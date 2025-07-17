using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Services;
using NArk.Services.Models;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeContractSweeper : IHostedService
{
    private readonly ArkTransactionBuilder _arkTransactionBuilder;
    private readonly ArkService.ArkServiceClient _arkServiceClient;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly ArkadeWalletSignerProvider _walletSignerProvider;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<ArkadeContractSweeper> _logger;
    private readonly IWalletService _walletService;
    private CompositeDisposable _leases = new();
    private CancellationTokenSource _cts = new();
    private TaskCompletionSource? _tcsWaitForNextPoll;

    public ArkadeContractSweeper(
        ArkTransactionBuilder arkTransactionBuilder,
        ArkService.ArkServiceClient arkServiceClient,
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        ArkadeWalletSignerProvider walletSignerProvider,
        EventAggregator eventAggregator,
        ILogger<ArkadeContractSweeper> logger,
        IWalletService walletService)
    {
        _arkTransactionBuilder = arkTransactionBuilder;
        _arkServiceClient = arkServiceClient;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _walletSignerProvider = walletSignerProvider;
        _eventAggregator = eventAggregator;
        _logger = logger;
        _walletService = walletService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _leases.Add(_eventAggregator.SubscribeAsync<VTXOsUpdated>(OnVTXOsUpdated));
        _ = PollForVTXOToSweep();
        return Task.CompletedTask;
    }

    private static ArkCoinWithSigner ToArkCoin(ArkWalletContract c, VTXO vtxo, IArkadeWalletSigner signer)
    {
        var cobtract = ArkContract.Parse(c.Type, c.ContractData);
        var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);
        var txout = new TxOut(Money.Satoshis(vtxo.Amount), cobtract.GetArkAddress());
        return new ArkCoinWithSigner(signer, cobtract, outpoint, txout);
    }


    private async Task PollForVTXOToSweep()
    {
        string[] allowedContractTypes = {VHTLCContract.ContractType, TweakedArkPaymentContract.ContractType};

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _tcsWaitForNextPoll = new TaskCompletionSource();

                await using var db = _arkPluginDbContextFactory.CreateContext();

                var vtxosAndContracts = await db.Vtxos
                    .Where(vtxo => vtxo.SpentByTransactionId == null && !vtxo.IsNote) // VTXO is unspent
                    .Join(
                        db.WalletContracts.Where(c => allowedContractTypes.Contains(c.Type)),
                        vtxo => vtxo.Script, // Key from VTXO
                        contract => contract.Script, // Key from WalletContract
                        (vtxo, contract) => new {Vtxo = vtxo, Contract = contract} // Select both VTXO and contract
                    )
                    .ToListAsync(_cts.Token);

                _logger.LogInformation($"Found {vtxosAndContracts.Count} VTXOs to sweep.");
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
                    await SweepWalletCoins(wallet, group.Select(x => (x.Vtxo, x.Contract)).ToArray(), signer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error while sweeping vtxos for wallet {wallet.Id}");
                    }

                }
                
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
            }
        }
    }

    private async Task SweepWalletCoins(ArkWallet wallet, (VTXO Vtxo, ArkWalletContract Contract)[] group, IArkadeWalletSigner signer)
    {
        var publicKey = wallet.PublicKey;

        // lets find htlcs with vtxos that we can sweep
        // for example, if we are the receiver + we have the preimage, we can sweep it
        // if we are the sender + we have the preimage + timelock has passed, we can sweep it
        // the other path is cooperatively signed, so we'd need a way to get a sig from boltz

        var coins = new List<ArkCoinWithSigner>();
        foreach (var vtxo in group)
        {
            var arkCoin = ToArkCoin(vtxo.Contract, vtxo.Vtxo, signer);
            switch (arkCoin.Contract)
            {
                case TweakedArkPaymentContract tweaked:
                    if (tweaked.OriginalUser.ToBytes().SequenceEqual(publicKey.ToBytes()))
                    {
                        coins.Add(arkCoin);
                    }

                    break;
                case VHTLCContract htlc:
                    if (htlc.Preimage is not null && htlc.Receiver.ToBytes().SequenceEqual(publicKey.ToBytes()) )
                    {
                        coins.Add(arkCoin);
                    }
                    else if (htlc.RefundLocktime.IsTimeLock &&
                             htlc.RefundLocktime.Date < DateTime.UtcNow && htlc.Sender.ToBytes().SequenceEqual(publicKey.ToBytes()) )
                    {
                        coins.Add(arkCoin);
                    }

                    break;
            }

            var sum = coins.Sum(x => x.TxOut.Value);

            if (sum == 0)
                continue;
            var contract =
                await _walletService.DerivePaymentContractAsync(new DeriveContractRequest(publicKey));
            var txout = new TxOut(sum, contract.GetArkAddress());
            await _arkTransactionBuilder.ConstructAndSubmitArkTransaction(
                coins,
                [txout],
                _arkServiceClient,
                _cts.Token);
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