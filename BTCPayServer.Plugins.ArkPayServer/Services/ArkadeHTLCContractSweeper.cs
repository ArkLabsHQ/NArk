using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Services;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeHTLCContractSweeper : IHostedService
{
    private readonly ArkTransactionBuilder _arkTransactionBuilder;
    private readonly ArkService.ArkServiceClient _arkServiceClient;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly ArkadeWalletSignerProvider _walletSignerProvider;
    private readonly EventAggregator _eventAggregator;
    private readonly IOperatorTermsService _operatorTermsService;
    private readonly ILogger<ArkadeHTLCContractSweeper> _logger;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private CompositeDisposable leases = new();
    CancellationTokenSource cts = new();

    public ArkadeHTLCContractSweeper(
        ArkTransactionBuilder arkTransactionBuilder,
        ArkService.ArkServiceClient arkServiceClient,
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        ArkadeWalletSignerProvider walletSignerProvider,
        EventAggregator eventAggregator,
        IOperatorTermsService operatorTermsService,
        ILogger<ArkadeHTLCContractSweeper> logger,
        BTCPayNetworkProvider btcPayNetworkProvider)
    {
        _arkTransactionBuilder = arkTransactionBuilder;
        _arkServiceClient = arkServiceClient;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _walletSignerProvider = walletSignerProvider;
        _eventAggregator = eventAggregator;
        _operatorTermsService = operatorTermsService;
        _logger = logger;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _network = _btcPayNetworkProvider.BTC.NBitcoinNetwork;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        leases.Add(_eventAggregator.SubscribeAsync<VTXOsUpdated>(OnVTXOsUpdated));
        _ = PollForVTXOToSweep();
        return Task.CompletedTask;
    }

    private static ArkCoin ToArkCoin(ArkWalletContract c, VTXO vtxo)
    {
        var cobtract = ArkContract.Parse(c.Type, c.ContractData);
        var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), vtxo.TransactionOutputIndex);
        var txout = new TxOut(Money.Satoshis(vtxo.Amount), cobtract.GetArkAddress());
        return new ArkCoin(cobtract, outpoint, txout);
    }

    TaskCompletionSource? tcsWaitForNextPoll = null;
    private readonly Network _network;

    private async Task PollForVTXOToSweep()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                tcsWaitForNextPoll = new TaskCompletionSource();

                await using var db = _arkPluginDbContextFactory.CreateContext();

                var vtxosAndContracts = await db.Vtxos
                    .Where(vtxo => vtxo.SpentByTransactionId == null && !vtxo.IsNote) // VTXO is unspent
                    .Join(
                        db.WalletContracts.Where(c => c.Type == VHTLCContract.ContractType), // Only tweaked contracts
                        vtxo => vtxo.Script, // Key from VTXO
                        contract => contract.Script, // Key from WalletContract
                        (vtxo, contract) => new {Vtxo = vtxo, Contract = contract} // Select both VTXO and contract
                    )
                    .ToListAsync(cts.Token);

                var groupedByWallet = vtxosAndContracts.GroupBy(x => x.Contract.WalletId).ToList();

                var walletsToCheck = groupedByWallet.Select(g => g.Key);

                var walletSigners = await _walletSignerProvider.GetSigners(walletsToCheck.ToArray(), cts.Token);

                foreach (var group in groupedByWallet)
                {
                    var signer = walletSigners.TryGet(group.Key);
                    if (signer is null)
                        continue;

                    var wallet = await db.Wallets.FirstOrDefaultAsync(x => x.Id == group.Key, cts.Token);
                    if (wallet is null)
                        continue;

                    var publicKey = wallet.PublicKey;

                    // lets find htlcs with vtxos that we can sweep
                    // for example, if we are the receiver + we have the preimage, we can sweep it
                    // if we are the sender + we have the preimage + timelock has passed, we can sweep it
                    // the other path is cooperatively signed, so we'd need a way to get a sig from boltz

                    var toSweepWithClaimPath = new List<ArkCoin>();
                    var toSweepWithRefundPath = new List<ArkCoin>();

                    foreach (var vtxo in group)
                    {
                        var arkCoin = ToArkCoin(vtxo.Contract, vtxo.Vtxo);
                        var htlc = (VHTLCContract) arkCoin.Contract;
                        if (htlc.Receiver == publicKey && htlc.Preimage is not null)
                        {
                            toSweepWithClaimPath.Add(arkCoin);
                        }
                        else if (htlc.Sender == publicKey && htlc.RefundLocktime.IsTimeLock &&
                                 htlc.RefundLocktime.Date < DateTime.UtcNow)
                        {
                            toSweepWithRefundPath.Add(arkCoin);
                        }
                    }

                    if (toSweepWithClaimPath.Count > 0 || toSweepWithRefundPath.Count > 0)
                    {
                        var total = Money.Satoshis(toSweepWithClaimPath.Concat(toSweepWithRefundPath)
                            .Sum(x => x.TxOut.Value));

                        var operatorTerms = await _operatorTermsService.GetOperatorTerms(cts.Token);
                        var destination = new ArkPaymentContract(operatorTerms.SignerKey,
                            operatorTerms.UnilateralExit, publicKey);

                        var txout = new TxOut(total, destination.GetArkAddress());

                        // Use the new ArkTransactionExtensions to create the Ark transaction
                        var coins = toSweepWithClaimPath.Concat(toSweepWithRefundPath)
                            .Select(coin => new ArkCoinWithSigner(signer, coin.Contract, coin.Outpoint, coin.TxOut))
                            .ToArray();


                        await _arkTransactionBuilder.ConstructAndSubmitArkTransaction(
                            coins,
                            [txout],
                            _arkServiceClient,
                            cts.Token);

                        // _eventAggregator.Publish(new VTXOsUpdated(finalizeTxResponse.Vtxos));
                    }
                }

                using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await tcsWaitForNextPoll.Task.WithCancellation(CancellationTokenSource
                    .CreateLinkedTokenSource(cts.Token, cts2.Token).Token);
            }catch (OperationCanceledException)
            {
                
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while sweeping HTLCs");
            }
        }
    }

    private Task OnVTXOsUpdated(VTXOsUpdated arg)
    {
        tcsWaitForNextPoll?.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await cts.CancelAsync();
        leases.Dispose();
        leases = new CompositeDisposable();
    }
}