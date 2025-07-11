using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Lightning.Events;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class BoltzSwapListener(
    EventAggregator eventAggregator,
    ArkPluginDbContextFactory dbContextFactory,
    InvoiceRepository invoiceRepository,
    ArkWalletService arkWalletService,
    ArkadePaymentMethodHandler arkadePaymentMethodHandler,
    ILogger<BoltzSwapListener> logger) : IHostedService
{
    private CompositeDisposable _leases = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _leases.Add(eventAggregator.SubscribeAsync<BoltzSwapStatusChangedEvent>(HandleSwapUpdate));
        return Task.CompletedTask;
    }
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _leases.Dispose();
        _leases = new CompositeDisposable();
        return Task.CompletedTask;
    }
    
    private async Task HandleSwapUpdate(BoltzSwapStatusChangedEvent e)
    {
        logger.LogInformation("Processing reverse swap {SwapId} status update to: {Status}", e.SwapId, e.Status);
        
        try
        {
            await using var dbContext = dbContextFactory.CreateContext();
            
            // Find the swap in the database
            var swap = await dbContext.LightningSwaps
                .FirstOrDefaultAsync(s => s.SwapId == e.SwapId && s.SwapType == "reverse");
                
            if (swap == null)
            {
                logger.LogWarning("Reverse swap {SwapId} not found in database", e.SwapId);
                return;
            }
            
            // Update the swap status
            var oldStatus = swap.Status;
            swap.Status = e.Status;
            
            // Set settlement time if swap is being marked as paid
            if (e.Status == "invoice.paid" && swap.SettledAt == null)
            {
                swap.SettledAt = DateTimeOffset.UtcNow;
                logger.LogInformation("Reverse swap {SwapId} marked as settled", e.SwapId);
            }
            
            await dbContext.SaveChangesAsync();
            
            logger.LogInformation("Updated reverse swap {SwapId} status from {OldStatus} to {NewStatus}", 
                e.SwapId, oldStatus, e.Status);
                
            if (e.Status == "invoice.paid")
            {
                eventAggregator.Publish(new ArkLightningInvoicePaidEvent(e.SwapId));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process reverse swap {SwapId} status update", e.SwapId);
            throw;
        }
    }
    
    public async Task ToggleArkadeContract(string invoiceId)
    {
        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        
        var active = invoice.Status == InvoiceStatus.New;
        var listenedContract = GetListenedArkadeInvoice(invoice);
        if (listenedContract is null)
        {
            return;
        }

        await arkWalletService.ToggleContract(listenedContract.Details.WalletId, listenedContract.Details.Contract,
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
                Details = arkadePaymentMethodHandler.ParsePaymentPromptDetails(prompt.Details)
            };
    }
}