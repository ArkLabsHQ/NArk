using System.Threading.Channels;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

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