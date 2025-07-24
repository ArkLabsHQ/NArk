using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Contracts;
using NArk.Services;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.Secp256k1;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeSpender
{
    private readonly AsyncKeyedLocker _asyncKeyedLocker;
    private readonly ArkTransactionBuilder _arkTransactionBuilder;
    private readonly ArkService.ArkServiceClient _arkServiceClient;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly ArkadeWalletSignerProvider _walletSignerProvider;
    private readonly ILogger<ArkadeSpender> _logger;
    private readonly IOperatorTermsService _operatorTermsService;

    public ArkadeSpender(AsyncKeyedLocker asyncKeyedLocker, 
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        ArkadeWalletSignerProvider arkadeWalletSignerProvider,
        ArkTransactionBuilder arkTransactionBuilder,
        ArkService.ArkServiceClient arkServiceClient,
        ILogger<ArkadeSpender> logger,
        IOperatorTermsService operatorTermsService)
    {
        _asyncKeyedLocker = asyncKeyedLocker;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _walletSignerProvider = arkadeWalletSignerProvider;
        _arkTransactionBuilder = arkTransactionBuilder;
        _arkServiceClient = arkServiceClient;
        _logger = logger;
        _operatorTermsService = operatorTermsService;
    }

    public async Task Spend(string walletId, TxOut[] outputs, CancellationToken cancellationToken = default)
    {
        using var l = await _asyncKeyedLocker.LockAsync($"ark-{walletId}-txs-spending", cancellationToken);
        
        var operatorTerms = await _operatorTermsService.GetOperatorTerms(cancellationToken);
        await using var db = _arkPluginDbContextFactory.CreateContext();
        
        var wallet = await db.Wallets.FirstOrDefaultAsync(x => x.Id == walletId, cancellationToken);
        if (wallet is null)
            throw new InvalidOperationException($"Wallet {walletId} not found");
            
        var signer = await _walletSignerProvider.GetSigner(walletId, cancellationToken);
        if (signer is null)
            throw new InvalidOperationException($"Signer for wallet {walletId} not found");

        string[] allowedContractTypes = {VHTLCContract.ContractType, HashLockedArkPaymentContract.ContractType, ArkPaymentContract.ContractType};
        
        var vtxosAndContracts = await db.Vtxos
            .Where(vtxo => vtxo.SpentByTransactionId == null && !vtxo.IsNote) // VTXO is unspent
            .Join(
                db.WalletContracts.Where(c => allowedContractTypes.Contains(c.Type) && c.WalletId == walletId),
                vtxo => vtxo.Script, // Key from VTXO
                contract => contract.Script, // Key from WalletContract
                (vtxo, contract) => new {Vtxo = vtxo, Contract = contract} // Select both VTXO and contract
            )
            .ToListAsync(cancellationToken);

        if (vtxosAndContracts.Count == 0)
        {
            _logger.LogWarning($"No spendable VTXOs found for wallet {walletId}");
            return;
        }

        _logger.LogInformation($"Found {vtxosAndContracts.Count} VTXOs to spend for wallet {walletId}");
        
        await SpendWalletCoins(wallet, vtxosAndContracts.Select(x => (x.Vtxo, x.Contract)).ToArray(), signer, operatorTerms, outputs, cancellationToken);
    }
    
    public async Task SweepWalletWithLock(ArkWallet wallet, (VTXO Vtxo, ArkWalletContract Contract)[] vtxosAndContracts, IArkadeWalletSigner signer, CancellationToken cancellationToken = default)
    {
        var operatorTerms = await _operatorTermsService.GetOperatorTerms(cancellationToken);
        
        if (vtxosAndContracts.Length == 0)
        {
            _logger.LogInformation($"No VTXOs to sweep for wallet {wallet.Id}");
            return;
        }

        _logger.LogInformation($"Found {vtxosAndContracts.Length} VTXOs to sweep for wallet {wallet.Id}");
        
        await SweepWalletCoins(wallet, vtxosAndContracts, signer, operatorTerms, cancellationToken);
    }

    private static ArkCoinWithSigner ToArkCoin(ArkWalletContract c, VTXO vtxo, IArkadeWalletSigner signer)
    {
        var contract = ArkContract.Parse(c.Type, c.ContractData);
        var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);
        var txout = new TxOut(Money.Satoshis(vtxo.Amount), contract.GetArkAddress());
        return new ArkCoinWithSigner(signer, contract, outpoint, txout);
    }

    private async Task SpendWalletCoins(ArkWallet wallet, (VTXO Vtxo, ArkWalletContract Contract)[] group, IArkadeWalletSigner signer, ArkOperatorTerms operatorTerms, TxOut[] outputs, CancellationToken cancellationToken)
    {
        var publicKey = wallet.PublicKey;
        var coins = GetSpendableCoins(group, signer, publicKey);
        
        if (coins.Count == 0)
        {
            _logger.LogInformation($"No spendable coins found for wallet {wallet.Id}");
            return;
        }

        var totalInput = coins.Sum(x => x.TxOut.Value);
        var totalOutput = outputs.Sum(x => x.Value);
        
        if (totalInput < totalOutput)
            throw new InvalidOperationException($"Insufficient funds. Available: {totalInput}, Required: {totalOutput}");

        var destination = wallet.Destination;
        if (destination is null)
        {
            var destinationContract = ContractUtils.DerivePaymentContract(new DeriveContractRequest(operatorTerms, publicKey));
            destination = destinationContract.GetArkAddress();
        }
        
        var change = totalInput - totalOutput;
        if (change > operatorTerms.Dust)
            outputs = outputs.Concat([new TxOut(Money.Satoshis(change), destination)]).ToArray();
        
        await _arkTransactionBuilder.ConstructAndSubmitArkTransaction(
            coins,
            outputs,
            _arkServiceClient,
            cancellationToken);
    }

    private async Task SweepWalletCoins(ArkWallet wallet, (VTXO Vtxo, ArkWalletContract Contract)[] group, IArkadeWalletSigner signer, ArkOperatorTerms operatorTerms, CancellationToken cancellationToken)
    {
        var publicKey = wallet.PublicKey;
        var coins = GetSpendableCoins(group, signer, publicKey);

        if (coins.Count == 0)
            return;

        var destination = wallet.Destination;
        if (destination is null)
        {
            var destinationContract = ContractUtils.DerivePaymentContract(new DeriveContractRequest(operatorTerms, publicKey));
            destination = destinationContract.GetArkAddress();
        }
        
        // Only sweep if we have coins not at the destination to avoid infinite sweeping loops
        if (!coins.Any(x => !x.TxOut.IsTo(destination)))
        {
            _logger.LogDebug($"Skipping sweep for wallet {wallet.Id}: all coins are already at destination");
            return;
        }

        var sum = coins.Sum(x => x.TxOut.Value);
        if (sum == 0)
            return;
        
        // Use the consolidated spend method for sweeping
        var sweepOutput = new TxOut(sum, destination);
        await SpendWalletCoins(wallet, group, signer, operatorTerms, [sweepOutput], cancellationToken);
    }

    private List<ArkCoinWithSigner> GetSpendableCoins((VTXO Vtxo, ArkWalletContract Contract)[] group, IArkadeWalletSigner signer, ECXOnlyPubKey user)
    {
        var coins = new List<ArkCoinWithSigner>();
        
        foreach (var vtxo in group)
        {
            if(vtxo.Vtxo.IsNote || vtxo.Vtxo.SpentByTransactionId is not null)
                continue;
            var arkCoin = ToArkCoin(vtxo.Contract, vtxo.Vtxo, signer);
            switch (arkCoin.Contract)
            {
                case ArkPaymentContract arkPaymentContract:
                    if (arkPaymentContract.User.ToBytes().SequenceEqual(user.ToBytes()))
                        coins.Add(arkCoin);
                    break;
                case HashLockedArkPaymentContract hashLockedArkPaymentContract:
                    if (hashLockedArkPaymentContract.User.ToBytes().SequenceEqual(user.ToBytes()))
                    {
                        coins.Add(arkCoin);
                    }
                    break;
                case VHTLCContract htlc:
                    if (htlc.Preimage is not null && htlc.Receiver.ToBytes().SequenceEqual(user.ToBytes()) || 
                        htlc.RefundLocktime.IsTimeLock &&
                        htlc.RefundLocktime.Date < DateTime.UtcNow && htlc.Sender.ToBytes().SequenceEqual(user.ToBytes()))
                    {
                        coins.Add(arkCoin);
                    }
                    break;
            }
        }
        
        return coins;
    }
}

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
        string[] allowedContractTypes = {VHTLCContract.ContractType, HashLockedArkPaymentContract.ContractType, ArkPaymentContract.ContractType};

        while (!_cts.IsCancellationRequested)
        {
            try
            {
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

                if(vtxosAndContracts.Count > 0)
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