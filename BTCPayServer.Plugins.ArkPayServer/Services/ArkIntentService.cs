using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Helpers;
using NArk.Services;
using NBitcoin;
using NBitcoin.Crypto;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Service for managing Ark intents with automatic submission, event monitoring, and batch participation
/// </summary>
public class ArkIntentService : IHostedService, IDisposable
{
    // Polling intervals
    private static readonly TimeSpan SubmissionPollingInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EventStreamRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoIntentsWaitInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultIntentExpiry = TimeSpan.FromMinutes(5);
    
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;
    private readonly ArkService.ArkServiceClient _arkServiceClient;
    private readonly ArkadeWalletSignerProvider _signerProvider;
    private readonly CachedOperatorTermsService _operatorTermsService;
    private readonly ArkTransactionBuilder _arkTransactionBuilder;
    private readonly ArkadeSpender _arkadeSpender;
    private readonly Network _network;
    private readonly ILogger<ArkIntentService> _logger;
    
    private readonly ConcurrentDictionary<string, ArkIntent> _activeIntents = new();
    private readonly ConcurrentDictionary<string, BatchSession> _activeBatchSessions = new();
    private CancellationTokenSource? _serviceCts;
    private CancellationTokenSource? _eventStreamCts;
    private Task? _submissionTask;
    private Task? _eventStreamTask;
    private readonly SemaphoreSlim _streamRestartLock = new(1, 1);
    private bool _needsStreamRestart;
    private TaskCompletionSource<bool>? _intentsChangedTcs;
    private TaskCompletionSource<bool>? _submissionTriggerTcs;

    public ArkIntentService(
        IDbContextFactory<ArkPluginDbContext> dbContextFactory,
        ArkService.ArkServiceClient arkServiceClient,
        ArkadeWalletSignerProvider signerProvider,
        CachedOperatorTermsService operatorTermsService,
        ArkTransactionBuilder arkTransactionBuilder,
        ArkadeSpender arkadeSpender,
        Network network,
        ILogger<ArkIntentService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _arkServiceClient = arkServiceClient;
        _signerProvider = signerProvider;
        _operatorTermsService = operatorTermsService;
        _arkTransactionBuilder = arkTransactionBuilder;
        _arkadeSpender = arkadeSpender;
        _network = network;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting ArkIntentService");
        
        _serviceCts = new CancellationTokenSource();
        
        // Start automatic submission task
        _submissionTask = AutoSubmitIntentsAsync(_serviceCts.Token);
        
        // Load existing WaitingForBatch intents and start shared event stream
        await LoadActiveIntentsAsync(cancellationToken);
        _eventStreamTask = RunSharedEventStreamAsync(_serviceCts.Token);
        
        _logger.LogInformation("ArkIntentService started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping ArkIntentService");
        if(_serviceCts != null)
            await _serviceCts!.CancelAsync();
        
        // Stop event stream
        if (_eventStreamCts != null)
            await _eventStreamCts.CancelAsync();
        
        // Wait for tasks to complete
        if (_submissionTask != null)
            await _submissionTask;
        if (_eventStreamTask != null)
            await _eventStreamTask;
        
        _activeIntents.Clear();
        _activeBatchSessions.Clear();
        _logger.LogInformation("ArkIntentService stopped");
    }

    /// <summary>
    /// Create a new intent
    /// </summary>
    public async Task<string> CreateIntentAsync(
        string walletId,
        SpendableArkCoinWithSigner[] coins,
        IntentTxOut[] outputs,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        
        // Check if any VTXOs are already locked
        var coinOutpoints = coins.Select(c => new { TransactionId = c.Outpoint.Hash.ToString(), TransactionOutputIndex = (int)c.Outpoint.N }).ToList();
        var lockedVtxos = await dbContext.Intents
            .Where(i => i.State == ArkIntentState.WaitingToSubmit || i.State == ArkIntentState.WaitingForBatch)
            .SelectMany(i => i.LockedVtxos)
            .Where(v => coinOutpoints.Contains(new { v.TransactionId, v.TransactionOutputIndex }))
            .ToListAsync(cancellationToken);
        
        if (lockedVtxos.Any())
        {
            throw new InvalidOperationException(
                $"One or more VTXOs are already locked by another intent: {string.Join(", ", lockedVtxos.Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}"))}");
        }

        // Get signer
        var signer = await _signerProvider.GetSigner(walletId, cancellationToken);
        if (signer == null)
        {
            throw new InvalidOperationException($"Signer not available for wallet {walletId}");
        }
        
        var vtxoScripts = coins.Select(c => c.Contract.GetArkAddress().ScriptPubKey.ToHex()).ToList();
        // ensure the wallet has the contract of the vtxos in question
       var contracts = await dbContext.WalletContracts.Where(wc => wc.WalletId == walletId && vtxoScripts.Contains(wc.Script))
           .ToDictionaryAsync(contract => contract.Script, cancellationToken);
       if (contracts.Count != vtxoScripts.Count)
       {
           throw new InvalidOperationException($"One or more VTXOs are not owned by wallet {walletId}");
       } 
       

        // Create intent transactions
        var effectiveValidFrom = validFrom ?? DateTimeOffset.UtcNow;
        var effectiveValidUntil = validUntil ?? DateTimeOffset.UtcNow.Add(DefaultIntentExpiry);
        
        var cosigners = new[] { await signer.GetXOnlyPublicKey(cancellationToken) };
        
        var (registerTx, deleteTx, registerMessage, deleteMessage) = await IntentUtils.CreateIntent(
            _network,
            cosigners,
            effectiveValidFrom,
            effectiveValidUntil,
            coins,
            outputs,
            signer,
            cancellationToken);

        // Convert coins to VTXO entities for database storage
        var vtxoEntities = coins.Select(coin => new VTXO
        {
            TransactionId = coin.Outpoint.Hash.ToString(),
            TransactionOutputIndex = (int)coin.Outpoint.N,
            Amount = coin.TxOut.Value.Satoshi,
            Script = coin.TxOut.ScriptPubKey.ToHex(),
            SeenAt = DateTimeOffset.UtcNow,
            Recoverable = false // Assuming these are regular VTXOs, not notes
        }).ToList();

        // Create intent entity
        var intent = new ArkIntent
        {
            Id = Guid.NewGuid().ToString(),
            WalletId = walletId,
            State = ArkIntentState.WaitingToSubmit,
            ValidFrom = effectiveValidFrom,
            ValidUntil = effectiveValidUntil,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LockedVtxos = vtxoEntities,
            RegisterProof = registerTx.ToBase64(),
            RegisterProofMessage = JsonSerializer.Serialize(registerMessage),
            DeleteProof = deleteTx.ToBase64(),
            DeleteProofMessage = JsonSerializer.Serialize(deleteMessage)
        };

        await dbContext.Intents.AddAsync(intent, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        
        _logger.LogInformation("Created intent {IntentId} for wallet {WalletId}", intent.Id, walletId);
        
        // Trigger immediate submission check if intent is already valid
        if (effectiveValidFrom <= DateTimeOffset.UtcNow)
        {
            TriggerSubmissionCheck();
        }
        
        return intent.Id;
    }

    /// <summary>
    /// Cancel an intent by submitting the delete proof
    /// </summary>
    public async Task CancelIntentAsync(string intentId, string reason, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        
        var intent = await dbContext.Intents
            .Include(i => i.LockedVtxos)
            .FirstOrDefaultAsync(i => i.Id == intentId, cancellationToken);
        
        if (intent == null)
        {
            _logger.LogWarning("Intent {IntentId} not found for cancellation", intentId);
            return;
        }

        switch (intent.State)
        {
            case ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed or ArkIntentState.Cancelled:
                _logger.LogWarning("Intent {IntentId} is already in terminal state {State}", intentId, intent.State);
                return;
            // Submit delete proof if intent was submitted
            case ArkIntentState.WaitingForBatch:
                try
                {
                    var deleteRequest = new DeleteIntentRequest
                    {
                        Intent = new Intent()
                        {
                            Message = intent.DeleteProofMessage,
                            Proof = intent.DeleteProof
                        }
                    };
                
                    await _arkServiceClient.DeleteIntentAsync(deleteRequest, cancellationToken: cancellationToken);
                    _logger.LogInformation("Submitted delete proof for intent {IntentId}", intentId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to submit delete proof for intent {IntentId}", intentId);
                }

                break;
        }

        intent.State = ArkIntentState.Cancelled;
        intent.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        
        // Remove from active intents and trigger stream restart
        _activeIntents.TryRemove(intentId, out _);
        _activeBatchSessions.TryRemove(intentId, out _);
        SignalIntentsChanged();
        await TriggerStreamRestartAsync();
        
        _logger.LogInformation("Intent {IntentId} cancelled: {Reason}", intentId, reason);
    }

    /// <summary>
    /// Get an intent by ID
    /// </summary>
    public async Task<ArkIntent?> GetIntentAsync(string intentId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Intents
            .Include(i => i.LockedVtxos)
            .FirstOrDefaultAsync(i => i.Id == intentId, cancellationToken);
    }

    /// <summary>
    /// Get all intents for a wallet
    /// </summary>
    public async Task<List<ArkIntent>> GetWalletIntentsAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Intents
            .Include(i => i.LockedVtxos)
            .Where(i => i.WalletId == walletId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    // /// <summary>
    // /// Check if a wallet has any pending intents
    // /// </summary>
    // public async Task<bool> HasPendingIntentAsync(string walletId, CancellationToken cancellationToken = default)
    // {
    //     await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
    //     return await dbContext.Intents
    //         .AnyAsync(i => i.WalletId == walletId && 
    //                       (i.State == ArkIntentState.WaitingToSubmit || i.State == ArkIntentState.WaitingForBatch),
    //                  cancellationToken);
    // }

    #region Private Methods

    private async Task AutoSubmitIntentsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting automatic intent submission loop");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for trigger or timeout
                _submissionTriggerTcs = new TaskCompletionSource<bool>();
                var delayTask = Task.Delay(SubmissionPollingInterval, cancellationToken);
                await Task.WhenAny(_submissionTriggerTcs.Task, delayTask);
                
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                
                var now = DateTimeOffset.UtcNow;
                var intentsToSubmit = await dbContext.Intents
                    .Include(i => i.LockedVtxos)
                    .Where(i => i.State == ArkIntentState.WaitingToSubmit && 
                                i.ValidFrom <= now && 
                                i.ValidUntil > now)
                    .ToListAsync(cancellationToken);
                
                foreach (var intent in intentsToSubmit)
                {
                    try
                    {
                        await SubmitIntentAsync(intent, dbContext, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to submit intent {IntentId}", intent.Id);
                    }
                }
                
                // Cancel expired intents
                var expiredIntents = await dbContext.Intents
                    .Where(i => (i.State == ArkIntentState.WaitingToSubmit || i.State == ArkIntentState.WaitingForBatch) && 
                                i.ValidUntil <= now)
                    .ToListAsync(cancellationToken);
                
                foreach (var intent in expiredIntents)
                {
                    intent.State = ArkIntentState.Cancelled;
                    intent.CancellationReason = "Expired";
                    intent.UpdatedAt = DateTimeOffset.UtcNow;
                }
                
                if (expiredIntents.Any())
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Cancelled {Count} expired intents", expiredIntents.Count);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in automatic intent submission loop");
            }
        }
        
        _logger.LogInformation("Automatic intent submission loop stopped");
    }

    private async Task SubmitIntentAsync(ArkIntent intent, ArkPluginDbContext dbContext, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Submitting intent {IntentId}", intent.Id);
        
        // Check if any VTXOs have been spent before submission
        var spentVtxos = intent.LockedVtxos
            .Where(v => v.SpentByTransactionId != null)
            .ToList();
        
        if (spentVtxos.Any())
        {
            var spentOutpoints = string.Join(", ", spentVtxos.Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}"));
            _logger.LogWarning("Intent {IntentId} has spent VTXOs: {SpentVtxos}", intent.Id, spentOutpoints);
            
            intent.State = ArkIntentState.Cancelled;
            intent.CancellationReason = $"VTXOs spent before submission: {spentOutpoints}";
            intent.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }
        
        var registerRequest = new RegisterIntentRequest
        {
            Intent = new Intent()
            {
                Message = intent.RegisterProofMessage,
                Proof = intent.RegisterProof
            }
        };

        var response = await _arkServiceClient.RegisterIntentAsync(registerRequest, cancellationToken: cancellationToken);
        
        intent.Id = response.IntentId; // Update with server-assigned ID
        intent.State = ArkIntentState.WaitingForBatch;
        intent.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        
        _logger.LogInformation("Intent submitted successfully with ID {IntentId}", intent.Id);
        
        // Add to active intents and trigger stream restart
        _activeIntents[intent.Id] = intent;
        SignalIntentsChanged();
        await TriggerStreamRestartAsync();
    }

    private async Task LoadActiveIntentsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Loading active intents");
        
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        
        var waitingIntents = await dbContext.Intents
            .Include(i => i.LockedVtxos)
            .Where(i => i.State == ArkIntentState.WaitingForBatch)
            .ToListAsync(cancellationToken);
        
        foreach (var intent in waitingIntents)
        {
            _activeIntents[intent.Id] = intent;
        }
        
        _logger.LogInformation("Loaded {Count} active intents", waitingIntents.Count);
    }

    private async Task RunSharedEventStreamAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting shared event stream");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Build topics from all active intents (VTXOs + cosigner public keys)
                var vtxoTopics = _activeIntents.Values
                    .SelectMany(intent => intent.LockedVtxos
                        .Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}"));
                
                var cosignerTopics = _activeIntents.Values
                    .SelectMany(intent => ExtractCosignerKeys(intent.RegisterProofMessage));
                
                var topics = vtxoTopics.Concat(cosignerTopics)
                    .Distinct()
                    .ToList();

                if (topics.Count == 0)
                {
                    _logger.LogDebug("No active intents, waiting for intents to be added");
                    
                    // Wait for intents to be added or timeout
                    _intentsChangedTcs = new TaskCompletionSource<bool>();
                    var delayTask = Task.Delay(NoIntentsWaitInterval, cancellationToken);
                    var completedTask = await Task.WhenAny(_intentsChangedTcs.Task, delayTask);
                    
                    if (completedTask == delayTask)
                    {
                        _logger.LogDebug("No intents added after {Seconds} seconds, retrying", NoIntentsWaitInterval.TotalSeconds);
                    }
                    else
                    {
                        _logger.LogDebug("Intents added, restarting stream immediately");
                    }
                    
                    continue;
                }

                var eventStreamRequest = new GetEventStreamRequest();
                eventStreamRequest.Topics.AddRange(topics);

                _logger.LogInformation("Opening shared event stream with {TopicCount} topics for {IntentCount} intents",
                    topics.Count, _activeIntents.Count);

                _eventStreamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _needsStreamRestart = false;

                using var streamCall = _arkServiceClient.GetEventStream(eventStreamRequest, cancellationToken: _eventStreamCts.Token);

                await foreach (var eventResponse in streamCall.ResponseStream.ReadAllAsync(_eventStreamCts.Token))
                {
                    // Check if we need to restart the stream with updated topics
                    if (_needsStreamRestart)
                    {
                        _logger.LogInformation("Stream restart requested, closing current stream");
                        break;
                    }

                    await ProcessEventForAllIntentsAsync(eventResponse, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Shared event stream cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in shared event stream, restarting in {Seconds} seconds", EventStreamRetryDelay.TotalSeconds);
                await Task.Delay(EventStreamRetryDelay, cancellationToken);
            }
            finally
            {
                _eventStreamCts?.Dispose();
                _eventStreamCts = null;
            }
        }

        _logger.LogInformation("Shared event stream stopped");
    }

    private async Task ProcessEventForAllIntentsAsync(GetEventStreamResponse eventResponse, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Handle BatchStarted event first - check all intents at once
        if (eventResponse.EventCase == GetEventStreamResponse.EventOneofCase.BatchStarted)
        {
            await HandleBatchStartedForAllIntentsAsync(eventResponse.BatchStarted, dbContext, cancellationToken);
        }

        // Process event for each active intent that might be affected
        foreach (var (intentId, intent) in _activeIntents.ToArray())
        {
            try
            {
                // If we have an active batch session, pass all events to it
                if (_activeBatchSessions.TryGetValue(intentId, out var batchSession))
                {
                    var isComplete = await batchSession.ProcessEventAsync(eventResponse, cancellationToken);
                    if (isComplete)
                    {
                        _activeBatchSessions.TryRemove(intentId, out _);
                        _activeIntents.TryRemove(intentId, out _);
                        SignalIntentsChanged();
                        await TriggerStreamRestartAsync();
                        continue;
                    }
                }

                // Handle events that affect this intent
                switch (eventResponse.EventCase)
                {
                    case GetEventStreamResponse.EventOneofCase.BatchFailed:
                        if (eventResponse.BatchFailed.Id == intent.BatchId)
                        {
                            await HandleBatchFailedAsync(intent, eventResponse.BatchFailed, dbContext, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            _activeIntents.TryRemove(intentId, out _);
                            SignalIntentsChanged();
                            await TriggerStreamRestartAsync();
                        }
                        break;

                    case GetEventStreamResponse.EventOneofCase.BatchFinalized:
                        if (eventResponse.BatchFinalized.Id == intent.BatchId)
                        {
                            await HandleBatchFinalizedAsync(intent, eventResponse.BatchFinalized, dbContext, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            _activeIntents.TryRemove(intentId, out _);
                            SignalIntentsChanged();
                            await TriggerStreamRestartAsync();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing event for intent {IntentId}", intentId);
            }
        }
    }

    private async Task HandleBatchStartedForAllIntentsAsync(
        BatchStartedEvent batchEvent,
        ArkPluginDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // Build a map of intent ID hashes to intent IDs for efficient lookup
        var intentHashMap = new Dictionary<string, string>();
        foreach (var (intentId, intent) in _activeIntents)
        {
            var intentIdBytes = Encoding.UTF8.GetBytes(intentId);
            var intentIdHash = Hashes.SHA256(intentIdBytes);
            var intentIdHashStr = Convert.ToHexString(intentIdHash).ToLowerInvariant();
            intentHashMap[intentIdHashStr] = intentId;
        }

        // Find all our intents that are included in this batch
        var selectedIntentIds = new List<string>();
        foreach (var intentIdHash in batchEvent.IntentIdHashes)
        {
            if (intentHashMap.TryGetValue(intentIdHash, out var intentId))
            {
                selectedIntentIds.Add(intentId);
            }
        }

        if (selectedIntentIds.Count == 0)
        {
            return; // None of our intents in this batch
        }

        _logger.LogInformation("{Count} of our intents selected for batch {BatchId}: {IntentIds}",
            selectedIntentIds.Count, batchEvent.Id, string.Join(", ", selectedIntentIds));

        // Load all VTXOs and contracts for selected intents in one efficient query
        var walletIds = selectedIntentIds
            .Select(id => _activeIntents.TryGetValue(id, out var intent) ? intent.WalletId : null)
            .Where(wid => wid != null)
            .Select(wid => wid!)
            .Distinct()
            .ToList();
        
        if (walletIds.Count == 0)
        {
            _logger.LogWarning("No valid wallet IDs found for selected intents");
            return;
        }
        
        // Collect all VTXO outpoints from all selected intents
        var allVtxoOutpoints = selectedIntentIds
            .Where(id => _activeIntents.ContainsKey(id))
            .SelectMany(id => _activeIntents[id].LockedVtxos
                .Select(v => new OutPoint(uint256.Parse(v.TransactionId), (uint)v.TransactionOutputIndex)))
            .ToHashSet();
        
        // Get spendable coins for all wallets, filtered by the specific VTXOs locked in intents
        var walletCoins = await _arkadeSpender.GetSpendableCoins(walletIds.ToArray(), allVtxoOutpoints, true, cancellationToken);
        
        // Confirm registration and create batch sessions for all selected intents
        foreach (var intentId in selectedIntentIds)
        {
            if (!_activeIntents.TryGetValue(intentId, out var intent))
                continue;

            try
            {
                // Get signer
                var signer = await _signerProvider.GetSigner(intent.WalletId, cancellationToken);
                if (signer == null)
                {
                    _logger.LogError("Signer not available for wallet {WalletId}", intent.WalletId);
                    continue;
                }
                
                // Get spendable coins for this wallet from the pre-loaded data
                if (!walletCoins.TryGetValue(intent.WalletId, out var allWalletCoins))
                {
                    _logger.LogError("No coins loaded for wallet {WalletId}", intent.WalletId);
                    continue;
                }
                
                // Filter to only the VTXOs locked by this intent
                var intentVtxoOutpoints = intent.LockedVtxos
                    .Select(v => new OutPoint(uint256.Parse(v.TransactionId), (uint)v.TransactionOutputIndex))
                    .ToHashSet();
                
                var spendableCoins = allWalletCoins
                    .Where(coin => intentVtxoOutpoints.Contains(coin.Outpoint))
                    .ToList();
                
                if (spendableCoins.Count == 0)
                {
                    _logger.LogError("No spendable coins found for intent {IntentId}", intentId);
                    continue;
                }
                
                // Confirm registration
                await _arkServiceClient.ConfirmRegistrationAsync(
                    new ConfirmRegistrationRequest { IntentId = intentId },
                    cancellationToken: cancellationToken);

                intent.BatchId = batchEvent.Id;
                intent.UpdatedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Intent {IntentId} confirmed for batch {BatchId}", intentId, batchEvent.Id);

                // Create and initialize batch session
                var session = new BatchSession(
                    _operatorTermsService,
                    _arkServiceClient,
                    _arkTransactionBuilder,
                    _network,
                    signer,
                    intent,
                    spendableCoins.ToArray(),
                    batchEvent,
                    _logger);
            
                await session.InitializeAsync(cancellationToken);
            
                // Store the session so events can be passed to it
                _activeBatchSessions[intent.Id] = session;
            
                _logger.LogInformation("Batch session initialized for intent {IntentId}", intent.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to confirm or create batch session for intent {IntentId}", intentId);
            }
        }
    }

    private void SignalIntentsChanged()
    {
        // Signal that intents have changed (for when waiting with no active intents)
        _intentsChangedTcs?.TrySetResult(true);
    }

    private void TriggerSubmissionCheck()
    {
        // Signal that a new intent needs to be checked for submission
        _submissionTriggerTcs?.TrySetResult(true);
    }

    private static IEnumerable<string> ExtractCosignerKeys(string registerProofMessage)
    {
        try
        {
            var message = JsonSerializer.Deserialize<RegisterIntentMessage>(registerProofMessage);
            return message?.CosignersPublicKeys ?? [];
        }
        catch (Exception)
        {
            // If we can't parse the message, return empty
            return [];
        }
    }

    

    private async Task TriggerStreamRestartAsync()
    {
        await _streamRestartLock.WaitAsync();
        try
        {
            _needsStreamRestart = true;
            if (_eventStreamCts != null)
            {
                await _eventStreamCts.CancelAsync();
            }
        }
        finally
        {
            _streamRestartLock.Release();
        }
    }

    private async Task HandleBatchFailedAsync(
        ArkIntent intent, 
        BatchFailedEvent batchEvent, 
        ArkPluginDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (intent.BatchId == batchEvent.Id)
        {
            _logger.LogWarning("Batch {BatchId} failed for intent {IntentId}: {Reason}", 
                batchEvent.Id, intent.Id, batchEvent.Reason);
            
            intent.State = ArkIntentState.BatchFailed;
            intent.CancellationReason = $"Batch failed: {batchEvent.Reason}";
            intent.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task HandleBatchFinalizedAsync(
        ArkIntent intent, 
        BatchFinalizedEvent finalizedEvent, 
        ArkPluginDbContext dbContext,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Batch finalized for intent {IntentId}, txid: {Txid}", 
            intent.Id, finalizedEvent.CommitmentTxid);

        intent.State = ArkIntentState.BatchSucceeded;
        intent.CommitmentTransactionId = finalizedEvent.CommitmentTxid;
        intent.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    #endregion

    public void Dispose()
    {
        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        _eventStreamCts?.Cancel();
        _eventStreamCts?.Dispose();
        _streamRestartLock.Dispose();
        
        _activeIntents.Clear();
        _activeBatchSessions.Clear();
    }
}
