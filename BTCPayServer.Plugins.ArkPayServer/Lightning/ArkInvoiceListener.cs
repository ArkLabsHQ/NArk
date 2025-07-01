using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Collections.Concurrent;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk.Wallet.Boltz;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkInvoiceListener : ILightningInvoiceListener
{
    private readonly string _walletId;
    private readonly BoltzClient _boltzClient;
    private readonly ArkPluginDbContextFactory _dbContextFactory;
    private readonly LightningSwapProcessor _swapProcessor;
    private readonly CancellationToken _cancellationToken;
    private readonly BoltzWebsocketClient _webSocketClient;
    private readonly CancellationTokenSource _listenerCts;
    private readonly ConcurrentQueue<LightningInvoice> _paidInvoices = new();
    private readonly SemaphoreSlim _waitSemaphore = new(0);
    private readonly HashSet<string> _returnedInvoiceIds = new();

    public ArkInvoiceListener(string walletId, BoltzClient boltzClient, ArkPluginDbContextFactory dbContextFactory, 
        LightningSwapProcessor swapProcessor, CancellationToken cancellationToken)
    {
        _walletId = walletId;
        _boltzClient = boltzClient;
        _dbContextFactory = dbContextFactory;
        _swapProcessor = swapProcessor;
        _cancellationToken = cancellationToken;
        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        // Initialize WebSocket client for Boltz
        // TODO: Make this configurable - for now use default Boltz mainnet WebSocket
        var wsUri = new Uri("wss://api.boltz.exchange/v2/ws");
        _webSocketClient = new BoltzWebsocketClient(wsUri);
        
        // Set up WebSocket event handler
        _webSocketClient.OnAnyEventReceived += OnWebSocketEvent;
        
        // Start the listener
        _ = Task.Run(StartListening, _listenerCts.Token);
    }

    private async Task StartListening()
    {
        try
        {
            // Connect to WebSocket
            await _webSocketClient.ConnectAsync(_listenerCts.Token);
            
            // Subscribe to all active reverse swaps for this wallet
            await SubscribeToActiveSwaps();
        }
        catch (Exception ex)
        {
            // Log error but don't fail the listener completely
            // In a real implementation, we'd use proper logging
        }
    }

    private async Task SubscribeToActiveSwaps()
    {
        await using var dbContext = _dbContextFactory.CreateContext();
        
        // Get all active reverse swaps for this wallet
        var activeSwaps = await dbContext.LightningSwaps
            .Where(s => s.WalletId == _walletId && 
                       s.SwapType == "reverse" && 
                       s.Status != "invoice.paid" && 
                       s.Status != "swap.failed")
            .Select(s => s.SwapId)
            .ToArrayAsync(_listenerCts.Token);
            
        if (activeSwaps.Length > 0)
        {
            await _webSocketClient.SubscribeAsync(activeSwaps, _listenerCts.Token);
        }
    }

    private async Task OnWebSocketEvent(WebSocketResponse response)
    {
        try
        {
            if (response.Event == "update" && response.Channel == "swap.update" && response.Args?.Count > 0)
            {
                // Parse the swap update
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]?.ToString();
                    var status = swapUpdate["status"]?.ToString();
                    
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(status))
                    {
                        // Process the swap update
                        await _swapProcessor.HandleReverseSwapUpdateAsync(id, status, _listenerCts.Token);
                        
                        // If the swap is paid, add to paid invoices queue
                        if (status == "invoice.paid")
                        {
                            await using var dbContext = _dbContextFactory.CreateContext();
                            var paidSwap = await dbContext.LightningSwaps
                                .FirstOrDefaultAsync(s => s.SwapId == id && s.WalletId == _walletId, _listenerCts.Token);
                                
                            if (paidSwap != null)
                            {
                                var invoice = CreateLightningInvoiceFromSwap(paidSwap);
                                _paidInvoices.Enqueue(invoice);
                                _waitSemaphore.Release(); // Signal that a new invoice is available
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but don't fail the entire listener
            // In a real implementation, we'd use proper logging
        }
    }

    private LightningInvoice CreateLightningInvoiceFromSwap(LightningSwap reverseSwap)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(reverseSwap.Invoice, Network.Main); // TODO: DO not hard code
        
        // Map Boltz status to Lightning status
        var lightningStatus = reverseSwap.Status switch
        {
            "invoice.set" or "invoice.paid" => LightningInvoiceStatus.Paid,
            "invoice.failedToPay" or "swap.expired" => LightningInvoiceStatus.Expired,
            _ => LightningInvoiceStatus.Unpaid
        };
        
        return new LightningInvoice
        {
            Id = reverseSwap.SwapId,
            Amount = LightMoney.Satoshis(reverseSwap.OnchainAmount),
            Status = lightningStatus,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), // TODO: Some other value?
            BOLT11 = reverseSwap.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            PaidAt = reverseSwap.SettledAt,
            AmountReceived = lightningStatus == LightningInvoiceStatus.Paid ? LightMoney.Satoshis(reverseSwap.OnchainAmount) : null
        };
    }

    public void Dispose()
    {
        _listenerCts.Cancel();
        _webSocketClient?.DisposeAsync();
        _listenerCts.Dispose();
        _waitSemaphore.Dispose();
    }

    public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
    {
        var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationToken, cancellation).Token;

        // First, check if there are any paid invoices in the database that we haven't returned yet
        await using var dbContext = _dbContextFactory.CreateContext();
        var paidSwaps = await dbContext.LightningSwaps
            .Where(s => s.WalletId == _walletId && 
                       s.SwapType == "reverse" && 
                       s.Status == "invoice.paid")
            .ToListAsync(combinedCancellation);
            
        // Find paid invoices that we haven't returned yet
        var unreturnedSwaps = paidSwaps.Where(s => !_returnedInvoiceIds.Contains(s.SwapId)).ToList();
        
        if (unreturnedSwaps.Any())
        {
            var firstUnreturned = unreturnedSwaps.First();
            _returnedInvoiceIds.Add(firstUnreturned.SwapId);
            return CreateLightningInvoiceFromSwap(firstUnreturned);
        }

        // No unreturned paid invoices, wait for new ones
        while (!combinedCancellation.IsCancellationRequested)
        {
            try
            {
                // Wait for a new paid invoice to be queued
                await _waitSemaphore.WaitAsync(combinedCancellation);
                
                if (_paidInvoices.TryDequeue(out var invoice))
                {
                    // Make sure we don't return the same invoice twice
                    if (!_returnedInvoiceIds.Contains(invoice.Id))
                    {
                        _returnedInvoiceIds.Add(invoice.Id);
                        return invoice;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        throw new OperationCanceledException();
    }
}