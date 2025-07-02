using System.Threading.Channels;
using AsyncKeyedLock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ark.V1;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using NArk;
using NArk.Services;
using NArk.Services.Models;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

class ArkadeListenedContract
{
    public ArkadePromptDetails Details { get; set; }
    public string InvoiceId { get; set; }
}

public class ArkContractInvoiceListener : IHostedService
{
    private readonly IMemoryCache _memoryCache;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly ArkadePaymentMethodHandler _arkadePaymentMethodHandler;
    private readonly EventAggregator _eventAggregator;
    private readonly ArkWalletService _arkWalletService;
    private readonly PaymentService _paymentService;
    private readonly ILogger<ArkContractInvoiceListener> _logger;

    readonly Channel<string> _CheckInvoices = Channel.CreateUnbounded<string>();
    CompositeDisposable leases = new();

    public ArkContractInvoiceListener(
        IMemoryCache memoryCache,
        InvoiceRepository invoiceRepository,
        ArkadePaymentMethodHandler arkadePaymentMethodHandler,
        EventAggregator eventAggregator,
        ArkWalletService arkWalletService,
        PaymentService paymentService,
        ILogger<ArkContractInvoiceListener> logger)
    {
        _memoryCache = memoryCache;
        _invoiceRepository = invoiceRepository;
        _arkadePaymentMethodHandler = arkadePaymentMethodHandler;
        _eventAggregator = eventAggregator;
        _arkWalletService = arkWalletService;
        _paymentService = paymentService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        leases.Add(_eventAggregator.SubscribeAsync<InvoiceEvent>(async inv =>
        {
            _CheckInvoices.Writer.TryWrite(inv.Invoice.Id);
        }));
        leases.Add(_eventAggregator.SubscribeAsync<VTXOsUpdated>(OnVTXOs));

        _ = PollAllInvoices(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task OnVTXOs(VTXOsUpdated arg)
    {
        foreach (var scriptVtxos in arg.Vtxos.GroupBy(c => c.Script))
        {
            var inv = await _invoiceRepository.GetInvoiceFromAddress(ArkadePlugin.ArkadePaymentMethodId, scriptVtxos.Key); 
        if (inv is null)
            continue;
            foreach (var vtxo in scriptVtxos)
            {
                await HandlePaymentData(vtxo, inv, _arkadePaymentMethodHandler);
            }
        }
        
    }


    private async Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
        _logger.LogInformation(
            $"Invoice {invoice.Id} received payment {payment.Value} {payment.Currency} {payment.Id}");

        _eventAggregator.Publish(
            new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
    }
    
    private async Task HandlePaymentData(VTXO vtxo, InvoiceEntity invoice, ArkadePaymentMethodHandler handler)
    {
        var pmi = ArkadePlugin.ArkadePaymentMethodId;
        var details = new ArkadePaymentData($"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}");
        var paymentData = new PaymentData
        {
            Status = PaymentStatus.Settled,
            Amount = Money.Satoshis(vtxo.Amount).ToDecimal(MoneyUnit.BTC),
            Created = vtxo.SeenAt,
            Id = details.Outpoint,
            Currency = "BTC",
        }.Set(invoice, handler, details);


        var alreadyExistingPaymentThatMatches = invoice.GetPayments(false).SingleOrDefault(c =>
            c.Id == paymentData.Id && c.PaymentMethodId == pmi);

        if (alreadyExistingPaymentThatMatches == null)
        {
            var payment = await _paymentService.AddPayment(paymentData);
            if (payment != null)
            {
                await ReceivedPayment(invoice, payment);
            }
        }
        else
        {
            //else update it with the new data
            alreadyExistingPaymentThatMatches.Status = PaymentStatus.Settled;
            alreadyExistingPaymentThatMatches.Details = JToken.FromObject(details, handler.Serializer);
            await _paymentService.UpdatePayments([alreadyExistingPaymentThatMatches]);
        }

        _eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }
    
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        leases.Dispose();
        leases = new CompositeDisposable();
        return Task.CompletedTask;
    }

    public async Task ToggleArkadeContract(InvoiceEntity invoice)
    {
        var active = invoice.Status == InvoiceStatus.New;
        var listenedContract = GetListenedArkadeInvoice(invoice);
        if (listenedContract is null)
        {
            return;
        }

        await _arkWalletService.ToggleContract(listenedContract.Details.WalletId, listenedContract.Details.Contract,
            active);
    }

    private ArkadeListenedContract? GetListenedArkadeInvoice(InvoiceEntity invoice)
    {
        var prompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadePaymentMethodId);
        return prompt is null
            ? null
            : new ArkadeListenedContract
            {
                InvoiceId = invoice.Id,
                Details = _arkadePaymentMethodHandler.ParsePaymentPromptDetails(prompt.Details)
            };
    }

    private static DateTimeOffset GetExpiration(InvoiceEntity invoice)
    {
        var expiredIn = DateTimeOffset.UtcNow - invoice.ExpirationTime;
        return DateTimeOffset.UtcNow + (expiredIn >= TimeSpan.FromMinutes(5.0) ? expiredIn : TimeSpan.FromMinutes(5.0));
    }

    private string GetCacheKey(string invoiceId)
    {
        return $"{nameof(GetListenedArkadeInvoice)}-{invoiceId}";
    }

    private Task<InvoiceEntity> GetInvoice(string invoiceId)
    {
        return _memoryCache.GetOrCreateAsync(GetCacheKey(invoiceId), async (cacheEntry) =>
        {
            var invoice = await _invoiceRepository.GetInvoice(invoiceId);
            if (invoice is null)
                return null;
            cacheEntry.AbsoluteExpiration = GetExpiration(invoice);
            return invoice;
        })!;
    }


    private async Task PollAllInvoices(CancellationToken cancellation)
    {
        retry:
        if (cancellation.IsCancellationRequested)
            return;
        try
        {
            foreach (var invoice in await _invoiceRepository.GetMonitoredInvoices(ArkadePlugin.ArkadePaymentMethodId,
                         cancellation))
            {
                if (GetListenedArkadeInvoice(invoice) is not null)
                {
                    _CheckInvoices.Writer.TryWrite(invoice.Id);
                    _memoryCache.Set(GetCacheKey(invoice.Id), invoice, GetExpiration(invoice));
                }
            }

            _logger.LogInformation("Checking if any payment arrived on Arkade while the server was offline... done.");


            while (await _CheckInvoices.Reader.WaitToReadAsync(cancellation) &&
                   _CheckInvoices.Reader.TryRead(out var invoiceId))
            {
                var invoice = await GetInvoice(invoiceId);
                await ToggleArkadeContract(invoice);
            }
        }
        catch when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Task.Delay(1000, cancellation);
            _logger.LogWarning(ex, "Unhandled error in the Arkade invoice listener.");
            goto retry;
        }
    }

}

public class ArkWalletService(
    AsyncKeyedLocker asyncKeyedLocker,
    ArkPluginDbContextFactory dbContextFactory,
    ArkService.ArkServiceClient arkClient,
    ArkSubscriptionService arkSubscriptionService,
    IWalletService walletService,
    ILogger<ArkWalletService> logger)
{
    
    
    public async Task<ArkContract> DerivePaymentContract(string walletId, CancellationToken cancellationToken)
    {
        return (await DeriveNewContract(walletId, async wallet =>
        {
            var paymentContract = await walletService.DerivePaymentContractAsync(new DeriveContractRequest(wallet.Wallet), cancellationToken);
            var address = paymentContract.GetArkAddress();
            var contract = new ArkWalletContract
            {
                WalletId = wallet.Id,
                Active = true,
                ContractData = paymentContract.GetContractData(),
                Script = address.ScriptPubKey.ToHex(),
            };

            return (contract, paymentContract);
        }, cancellationToken))!;
    }

    public async Task<ArkContract?> DeriveNewContract(string walletId,
        Func<ArkWallet, Task<(ArkWalletContract, ArkContract)?>> setup, CancellationToken cancellationToken)
    {
        using var locker = await asyncKeyedLocker.LockAsync($"DeriveNewContract{walletId}", cancellationToken);
        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = await dbContext.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
        if (wallet is null)
            throw new InvalidOperationException($"Wallet with ID {walletId} not found.");

        var contract = await setup(wallet);
        if (contract is null)
        {
            throw new InvalidOperationException($"Could not derive contract for wallet {walletId}");
        }

        await dbContext.WalletContracts.AddAsync(contract.Value.Item1, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("New contract derived for wallet {WalletId}: {Script}", walletId, contract.Value.Item1.Script);
        
        await arkSubscriptionService.UpdateManualSubscriptionAsync(contract.Value.Item1.Script, contract.Value.Item1.Active, cancellationToken);

        return contract.Value.Item2;
    }

    // public async Task<ArkWallet> CreateNewWalletAsync(string wallet,
    //     CancellationToken cancellationToken = default)
    // {
    //     logger.LogInformation("Creating new Ark wallet");
    //
    //     var key = walletService.GetXOnlyPubKeyFromWallet(wallet);
    //     
    //     try
    //     {
    //         var arkWallet = new ArkWallet
    //         {
    //             Id = walletService.GetWalletId(key),
    //             Wallet = wallet
    //         };
    //
    //         await using var dbContext = dbContextFactory.CreateContext();
    //
    //         await dbContext.Wallets.AddAsync(arkWallet, cancellationToken);
    //         await dbContext.SaveChangesAsync(cancellationToken);
    //
    //         logger.LogInformation("Successfully created and stored new Ark wallet with ID {WalletId}", arkWallet.Id);
    //
    //         return arkWallet;
    //     }
    //     catch (DbUpdateException ex) when (ex.InnerException?.Message?.Contains("UNIQUE constraint failed") == true)
    //     {
    //         throw new InvalidOperationException(
    //             "A wallet with this public key already exists. Please use a different seed.");
    //     }
    //     catch (Exception ex)
    //     {
    //         logger.LogError(ex, "Unexpected error occurred while creating wallet");
    //         throw;
    //     }
    // }

    public async Task<ArkWallet?> GetWalletAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        return await dbContext.Wallets
            .Include(w => w.Contracts)
            .FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
    }

    public async Task<List<ArkWallet>> GetAllWalletsAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        return await dbContext.Wallets
            .Include(w => w.Contracts)
            .ToListAsync(cancellationToken);
    }

    public async Task<ArkWallet> Upsert(string wallet)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var res = await dbContext.Wallets.Upsert(new ArkWallet()
        {
            Id = walletService.GetWalletId(wallet),
            Wallet = wallet,
        }).RunAndReturnAsync();
        
        return res.First();
    }
    
    //
    // /// <summary>
    // /// Creates a new boarding address for the specified wallet using the Ark operator's GetBoardingAddress gRPC call
    // /// </summary>
    // /// <param name="walletId">The wallet ID to create the boarding address for</param>
    // /// <param name="cancellationToken">Cancellation token</param>
    // /// <returns>The created boarding address information</returns>
    // public async Task<BoardingAddress> DeriveNewBoardingAddress(
    //     Guid walletId,
    //     CancellationToken cancellationToken = default)
    // {
    //     //TODO: Since this is onchain, we need to listen on nbx to this and a bunch of other things
    //     
    //     await using var dbContext = dbContextFactory.CreateContext();
    //
    //     var wallet = await dbContext.Wallets.FindAsync(walletId, cancellationToken);
    //     if (wallet is null)
    //     {
    //         throw new InvalidOperationException($"Wallet with ID {walletId} not found.");
    //     }
    //
    //     var latestBoardingAddress = await dbContext.BoardingAddresses.Where(w => w.WalletId == walletId)
    //         .OrderByDescending(w => w.DerivationIndex)
    //         .FirstOrDefaultAsync(cancellationToken);
    //
    //     var newDerivationIndex = latestBoardingAddress is null ? 0 : latestBoardingAddress.DerivationIndex + 1;
    //
    //     var xPub = ExtPubKey.Parse(wallet.Wallet, networkProvider.BTC.NBitcoinNetwork);
    //
    //     // TODO: We should probably pick some more deliberate derivation path
    //     var derivedPubKey = xPub.Derive(newDerivationIndex).PubKey.ToHex();
    //
    //     var response = await arkClient.GetBoardingAddressAsync(new GetBoardingAddressRequest
    //     {
    //         Pubkey = derivedPubKey
    //     }, cancellationToken: cancellationToken);
    //
    //     // Get operator info for additional metadata
    //     var operatorInfo = await arkClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
    //
    //     try
    //     {
    //         var boardingAddressEntity = new BoardingAddress
    //         {
    //             OnchainAddress = response.Address,
    //             WalletId = walletId,
    //             DerivationIndex = newDerivationIndex,
    //             BoardingExitDelay = (uint)operatorInfo.BoardingExitDelay,
    //             ContractData = response.HasDescriptor_ ? response.Descriptor_ : response.Tapscripts?.ToString() ?? "",
    //             CreatedAt = DateTimeOffset.UtcNow,
    //         };
    //
    //         await dbContext.BoardingAddresses.AddAsync(boardingAddressEntity, cancellationToken);
    //         await dbContext.SaveChangesAsync(cancellationToken);
    //
    //         logger.LogInformation("New boarding address created for wallet {WalletId}: {Address}",
    //             walletId, response.Address);
    //
    //         return boardingAddressEntity;
    //     }
    //     catch (DbUpdateException ex) when (ex.InnerException?.Message?.Contains("UNIQUE constraint failed") == true ||
    //                                        ex.InnerException?.Message?.Contains("duplicate key") == true)
    //     {
    //         logger.LogError("Failed to create boarding address due to unique constraint violation: {Error}",
    //             ex.Message);
    //         throw new InvalidOperationException(
    //             "A boarding address with this address already exists. Please try again.");
    //     }
    // }
    //
    // /// <summary>
    // /// Gets all boarding addresses for a wallet
    // /// </summary>
    // public async Task<List<BoardingAddress>> GetBoardingAddressesAsync(Guid walletId,
    //     CancellationToken cancellationToken = default)
    // {
    //     await using var dbContext = dbContextFactory.CreateContext();
    //     return await dbContext.BoardingAddresses
    //         .Where(b => b.WalletId == walletId)
    //         .OrderByDescending(b => b.CreatedAt)
    //         .ToListAsync(cancellationToken);
    // }
    public async Task ToggleContract(string detailsWalletId, ArkContract detailsContract, bool active)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var script = detailsContract.GetArkAddress().ScriptPubKey.ToHex();
        var contract = await dbContext.WalletContracts.FirstOrDefaultAsync(w => w.WalletId == detailsWalletId && w.Script == script);
        if (contract is  null)
        {
            return;
        }

        contract.Active = active;
        if (await dbContext.SaveChangesAsync() > 0)
        {
           await  arkSubscriptionService.UpdateManualSubscriptionAsync(script, active, CancellationToken.None);
        }
        

    }
}