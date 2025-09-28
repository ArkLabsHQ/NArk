using System.Collections.Concurrent;
using AsyncKeyedLock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using NArk;
using NArk.Contracts;
using NArk.Services;
using NArk.Models;
using NBitcoin;
using NBitcoin.Secp256k1;
using NArk.Extensions;
using NArk.Services.Abstractions;
using BTCPayServer.Plugins.ArkPayServer.Cache;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkWalletService(
    ActiveContractsCache activeContractsCache,
    ArkPluginDbContextFactory dbContextFactory,
    IOperatorTermsService operatorTermsService,
    ArkVtxoSyncronizationService arkVtxoSyncronizationService,
    IMemoryCache memoryCache,
    ILogger<ArkWalletService> logger) : IHostedService, IArkadeMultiWalletSigner
{

    private TaskCompletionSource started = new();
    private ConcurrentDictionary<string, ECPrivKey> walletSigners = new();

    public async Task<ArkWallet?> GetWallet(string walletId, CancellationToken cancellationToken)
    {
        var wallets = await GetWallets([walletId], cancellationToken);
        return wallets.SingleOrDefault();
        
    }
    public async Task<ArkWallet[]> GetWallets(string[] walletIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ArkWallet>();
        foreach (var walletId in walletIds)
        {
            if (memoryCache.TryGetValue("ark-wallet-" + walletId, out ArkWallet? wallet) && wallet is not null)
            {
                result.Add(walletId, wallet);
            }
        }
        var remaining = walletIds.Except(result.Keys);
        if (remaining.Any())
        {
            await using var dbContext = dbContextFactory.CreateContext();
            var wallets = await dbContext.Wallets.Where(w => remaining.Contains(w.Id)).ToArrayAsync(cancellationToken);
            foreach (var wallet in wallets)
            {
                memoryCache.Set("ark-wallet-" + wallet.Id, wallet);
                result.Add(wallet.Id, wallet);
            }
            
        }
        
        
        return result.Values.ToArray();
    }

    public async Task<decimal> GetBalanceInSats(string walletId, CancellationToken cancellation)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        var contracts = await dbContext.WalletContracts
            .Where(c => c.WalletId == walletId)
            .Select(c => c.Script)
            .ToListAsync(cancellation);

        var sum = await dbContext.Vtxos
            .Where(vtxo => contracts.Contains(vtxo.Script))
            .Where(vtxo => (vtxo.SpentByTransactionId == null || vtxo.SpentByTransactionId == "") && !vtxo.IsNote)
            .SumAsync(vtxo => vtxo.Amount, cancellationToken: cancellation);


        return sum;
    }

    public async Task<ArkContract> DerivePaymentContract(string walletId, CancellationToken cancellationToken)
    {
        var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        return (await DeriveNewContract(walletId, async wallet =>
        {
            var paymentContract = ContractUtils.DerivePaymentContract(
                new DeriveContractRequest(terms, wallet.PublicKey, RandomUtils.GetBytes(32)));
            var address = paymentContract.GetArkAddress();
            var contract = new ArkWalletContract
            {
                WalletId = walletId,
                Active = true,
                ContractData = paymentContract.GetContractData(),
                Script = address.ScriptPubKey.ToHex(),
                Type = paymentContract.Type,

            };

            return (contract, paymentContract);
        }, cancellationToken))!;
    }

    public async Task<ArkContract?> DeriveNewContract(string walletId,
        Func<ArkWallet, Task<(ArkWalletContract newContractData, ArkContract newContract)?>> setup,
        CancellationToken cancellationToken)
    {
        var wallet = await GetWallet(walletId, cancellationToken);
        if (wallet is null)
            throw new InvalidOperationException($"Wallet with ID {walletId} not found.");

        // using var locker = await asyncKeyedLocker.LockAsync($"DeriveNewContract{walletId}", cancellationToken);
        await using var dbContext = dbContextFactory.CreateContext();

        var contract = await setup(wallet);
        if (contract is null)
        {
            throw new InvalidOperationException($"Could not derive contract for wallet {walletId}");
        }

        var result = await dbContext.WalletContracts.Upsert(contract.Value.newContractData)
            .RunAndReturnAsync();
        
        if(result.Count != 0)
            logger.LogInformation("New contract derived for wallet {WalletId}: {Script}", walletId,
            contract.Value.newContractData.Script);

        activeContractsCache.TriggerUpdate();

        return contract.Value.newContract;
    }

    public async Task<string> Upsert(string walletValue, string? destination, bool owner,
        CancellationToken cancellationToken = default)
    {
        var publicKey = KeyExtensions.GetXOnlyPubKeyFromWallet(walletValue);
        var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        if (destination is not null)
        {
            var addr = ArkAddress.Parse(destination);
            if (!terms.SignerKey.ToBytes().SequenceEqual(addr.ServerKey.ToBytes()))
            {
                throw new InvalidOperationException("Invalid destination server key.");
            }
            
        }

        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = new ArkWallet()
        {
            Id = publicKey.ToHex(),
            WalletDestination = destination,
            Wallet = walletValue,
        };
        var commandBuilder = dbContext.Wallets.Upsert(wallet);

        
        if (!owner)
        {
            commandBuilder = commandBuilder.NoUpdate();
        }

        if (await commandBuilder.RunAsync(cancellationToken) > 0)
        {
            memoryCache.Set("ark-wallet-" + publicKey.ToHex(),wallet);
        }
        LoadWalletSigner(publicKey.ToHex(), walletValue);

        await DeriveNewContract(publicKey.ToHex(), wallet =>
        {
            var contract = ContractUtils.DerivePaymentContract(new DeriveContractRequest(terms, wallet.PublicKey));
            return Task.FromResult<(ArkWalletContract newContractData, ArkContract newContract)?>((new ArkWalletContract
            {
                WalletId = publicKey.ToHex(),
                Active = true,
                ContractData = contract.GetContractData(),
                Script = contract.GetArkAddress().ScriptPubKey.ToHex(),
                Type = contract.Type,
            }, contract));
        }, cancellationToken);
        
        return publicKey.ToHex();
    }

    public async Task ToggleContract(string detailsWalletId, ArkContract detailsContract, bool active)
    {
        await ToggleContract(detailsWalletId, detailsContract.GetArkAddress().ScriptPubKey.ToHex(), active);
    }

    public async Task ToggleContract(string detailsWalletId, string script, bool active)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var contract = await dbContext.WalletContracts.FirstOrDefaultAsync(w =>
            w.WalletId == detailsWalletId &&
            w.Script == script && w.Active != active);
        if (contract is null)
        {
            return;
        }

        logger.LogInformation("Toggling contract {Script} ({active}) for wallet {WalletId}", script, active,
            detailsWalletId);

        contract.Active = active;
        if (await dbContext.SaveChangesAsync() > 0)
        {
            activeContractsCache.TriggerUpdate();
        }
    }

    public async Task<bool> WalletExists(string walletId, CancellationToken cancellationToken = default)
    {
       return await GetWallet(walletId, cancellationToken) is not null;
    }
    
    [Obsolete("This function was broken down into multiple calls, use that.")]
    public async Task<(Dictionary<ArkWalletContract, VTXO[]>? Contracts, string? Destination, string? Wallet)?> GetWalletInfo(string walletId, bool includeData, CancellationToken cancellationToken = default)
    {

        var wallet = await GetWallet(walletId, cancellationToken);
        if (wallet is null)
        {
            return null;
        }

        Dictionary<ArkWalletContract, VTXO[]>? contracts = null;
        if (includeData)
        {
            var ccc = await GetVTXOsAndContracts([walletId], true, true, cancellationToken);
            ccc.TryGetValue(walletId, out contracts);
        }
        return (contracts, wallet.WalletDestination, includeData ? wallet.Wallet : null);
    }

    public async Task<string?> GetWalletDestination(string walletId, CancellationToken cancellationToken = default)
    {
        var wallet =
            await GetWallet(walletId, cancellationToken);
        return wallet?.WalletDestination;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        // load all wallets that have a private key as a signer
        var wallets = await dbContext.Wallets.Where(wallet => wallet.Wallet.StartsWith("nsec"))
            .Select(wallet => new { wallet.Id, wallet.Wallet }).ToListAsync(cancellationToken);
        foreach (var wallet in wallets)
        {
            LoadWalletSigner(wallet.Id, wallet.Wallet);
        }

        started.SetResult();
    }

    private void LoadWalletSigner(string id, string wallet)
    {
        try
        {
            walletSigners[id] = KeyExtensions.GetKeyFromWallet(wallet);

        }
        catch (Exception e)
        {
            // ignored
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        started = new TaskCompletionSource();
        walletSigners = new();
        return Task.CompletedTask;
    }

    public async Task<bool> CanHandle(string walletId, CancellationToken cancellationToken = default)
    {
        await started.Task;
        return walletSigners.ContainsKey(walletId);
    }

    public Task<IArkadeWalletSigner> CreateSigner(string walletId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IArkadeWalletSigner>(new MemoryWalletSigner(walletSigners[walletId]));
    }

    public async Task UpdateBalances(string configWalletId, bool onlyActive,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var wallets = await dbContext.WalletContracts
            .Where(c => c.WalletId == configWalletId && (!onlyActive || c.Active))
            .Select(c => c.Script)
            .ToListAsync(cancellationToken);

        await arkVtxoSyncronizationService.PollScriptsForVtxos(wallets.ToHashSet(), cancellationToken);
    }

    public async Task<IReadOnlyCollection<ArkWalletContract>> GetArkWalletContractsAsync(string walletId, int skip = 0, int count = 10, CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        return await dbContext.WalletContracts
            .Include(c => c.Swaps)
            .Where(c => c.WalletId.Equals(walletId))
            .OrderByDescending(c => c.CreatedAt)
            .Skip(skip)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Dictionary<string, Dictionary<ArkWalletContract, VTXO[]>>> GetVTXOsAndContracts(string[]? walletIds, bool allowSpent, bool allowNote, CancellationToken cancellationToken)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        // Get contracts first (no duplication)
        var contractsQuery = dbContext.WalletContracts
            .Include(c => c.Swaps)
            .Where(c => walletIds == null || walletIds.Contains(c.WalletId))
            .OrderByDescending(c => c.CreatedAt);

        var contracts = await contractsQuery.ToArrayAsync(cancellationToken);

        if (contracts.Length == 0)
            return [];

        // Get VTXOs that match any of the contract scripts
        var contractScripts = contracts.Select(c => c.Script).ToHashSet();
        var vtxos = await dbContext.Vtxos
            .Where(vtxo =>
                          (allowSpent || vtxo.SpentByTransactionId == null) &&
                          (allowNote || !vtxo.IsNote) &&
                          contractScripts.Contains(vtxo.Script))
            .OrderByDescending(vtxo => vtxo.SeenAt)
            .ToArrayAsync(cancellationToken);

        // Join in memory and create nested dictionary structure
        var contractLookup = contracts.ToLookup(c => c.Script);

        return vtxos
            .Where(vtxo => contractLookup.Contains(vtxo.Script))
            .SelectMany(vtxo => contractLookup[vtxo.Script].Select(contract => new { Vtxo = vtxo, Contract = contract }))
            .GroupBy(x => x.Contract.WalletId)
            .ToDictionary(
                walletGroup => walletGroup.Key,
                walletGroup => walletGroup
                    .GroupBy(x => x.Contract)
                    .ToDictionary(
                        contractGroup => contractGroup.Key,
                        contractGroup => contractGroup.Select(x => x.Vtxo).ToArray()
                    )
            );
    }
}
