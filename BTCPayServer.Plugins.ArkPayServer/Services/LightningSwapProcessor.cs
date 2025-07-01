using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class LightningSwapProcessor
{
    private readonly ArkPluginDbContextFactory _dbContextFactory;
    private readonly ILogger<LightningSwapProcessor> _logger;

    public LightningSwapProcessor(
        ArkPluginDbContextFactory dbContextFactory,
        ILogger<LightningSwapProcessor> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task HandleReverseSwapUpdateAsync(string swapId, string status, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing reverse swap {SwapId} status update to: {Status}", swapId, status);
        
        try
        {
            await using var dbContext = _dbContextFactory.CreateContext();
            
            // Find the swap in the database
            var swap = await dbContext.LightningSwaps
                .FirstOrDefaultAsync(s => s.SwapId == swapId && s.SwapType == "reverse", cancellationToken);
                
            if (swap == null)
            {
                _logger.LogWarning("Reverse swap {SwapId} not found in database", swapId);
                return;
            }
            
            // Update the swap status
            var oldStatus = swap.Status;
            swap.Status = status;
            
            // Set settlement time if swap is being marked as paid
            if (status == "invoice.paid" && swap.SettledAt == null)
            {
                swap.SettledAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("Reverse swap {SwapId} marked as settled", swapId);
            }
            
            await dbContext.SaveChangesAsync(cancellationToken);
            
            _logger.LogInformation("Updated reverse swap {SwapId} status from {OldStatus} to {NewStatus}", 
                swapId, oldStatus, status);
                
            // TODO: If status is "invoice.paid", trigger VTXO creation through ArkSubscriptionService
            // This should integrate with the existing payment detection flow
            if (status == "invoice.paid")
            {
                _logger.LogInformation("Reverse swap {SwapId} paid - VTXO creation should be triggered", swapId);
                // In the future, this should notify the payment system that the invoice is paid
                // and trigger the Ark VTXO creation process
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process reverse swap {SwapId} status update", swapId);
            throw;
        }
    }
    
    public async Task HandleSubmarineSwapUpdateAsync(string swapId, string status, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing submarine swap {SwapId} status update to: {Status}", swapId, status);
        
        try
        {
            using var dbContext = _dbContextFactory.CreateContext();
            
            // Find the swap in the database
            var swap = await dbContext.LightningSwaps
                .FirstOrDefaultAsync(s => s.SwapId == swapId && s.SwapType == "submarine", cancellationToken);
                
            if (swap == null)
            {
                _logger.LogWarning("Submarine swap {SwapId} not found in database", swapId);
                return;
            }
            
            // Update the swap status
            var oldStatus = swap.Status;
            swap.Status = status;
            
            // Set settlement time if swap is being completed
            if ((status == "transaction.confirmed" || status == "invoice.paid") && swap.SettledAt == null)
            {
                swap.SettledAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("Submarine swap {SwapId} marked as settled", swapId);
            }
            
            await dbContext.SaveChangesAsync(cancellationToken);
            
            _logger.LogInformation("Updated submarine swap {SwapId} status from {OldStatus} to {NewStatus}", 
                swapId, oldStatus, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process submarine swap {SwapId} status update", swapId);
            throw;
        }
    }
}