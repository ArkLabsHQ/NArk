using System.Collections.Concurrent;
using AsyncKeyedLock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.Extensions.Hosting;
using NArk;
using NArk.Contracts;
using NArk.Services;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Services;





public class ArkWalletService(
    AsyncKeyedLocker asyncKeyedLocker,
    ArkPluginDbContextFactory dbContextFactory,
    IOperatorTermsService operatorTermsService,
    ArkSubscriptionService arkSubscriptionService,
    ILogger<ArkWalletService> logger) : IHostedService, IArkadeMultiWalletSigner
{


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
        using var locker = await asyncKeyedLocker.LockAsync($"DeriveNewContract{walletId}", cancellationToken);
        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = await dbContext.Wallets.FindAsync([walletId], cancellationToken);
        if (wallet is null)
            throw new InvalidOperationException($"Wallet with ID {walletId} not found.");

        var contract = await setup(wallet);
        if (contract is null)
        {
            throw new InvalidOperationException($"Could not derive contract for wallet {walletId}");
        }

        var result = await dbContext.WalletContracts.Upsert(contract.Value.Item1)
            .RunAndReturnAsync();
        
        if(result.Any())
            logger.LogInformation("New contract derived for wallet {WalletId}: {Script}", walletId,
            contract.Value.Item1.Script);

        await arkSubscriptionService.UpdateManualSubscriptionAsync(contract.Value.Item1.Script,
            contract.Value.Item1.Active, cancellationToken);

        return contract.Value.Item2;
    }

    public async Task<string> Upsert(string walletValue, string? destination, bool owner,
        CancellationToken cancellationToken = default)
    {
        var publicKey = ArkExtensions.GetXOnlyPubKeyFromWallet(walletValue);
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
        var commandBuilder = dbContext.Wallets.Upsert(new ArkWallet()
        {
            Id = publicKey.ToHex(),
            WalletDestination = destination,
            Wallet = walletValue,
        });

        if (!owner)
        {
            commandBuilder = commandBuilder.NoUpdate();
        }

        await commandBuilder.RunAsync(cancellationToken);
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
            await arkSubscriptionService.UpdateManualSubscriptionAsync(script, active, CancellationToken.None);
        }
    }

    public async Task<bool> WalletExists(string walletId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        return await dbContext.Wallets.FindAsync([walletId],cancellationToken) is not null;
    }
    public async Task<(Dictionary<ArkWalletContract, VTXO[]> Contracts, string? Destination, string Wallet)?> GetWalletInfo(string walletId)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = await dbContext.Wallets.Include(w => w.Contracts)
            .ThenInclude(contract => contract.Swaps)
            .FirstOrDefaultAsync(w => w.Id == walletId);
        if (wallet is null)
        {
            return null;
        }

        var contractScripts = wallet.Contracts.Select(c => c.Script).ToArray();
        var vtxos = await dbContext.Vtxos.Where(vtxo1 => contractScripts.Contains(vtxo1.Script)).ToListAsync();

        var result = new Dictionary<ArkWalletContract, VTXO[]>();
        foreach (var contract in wallet.Contracts)
        {
            var filtered = vtxos.Where(vtxo1 => vtxo1.Script == contract.Script).OrderBy(vtxo1 => vtxo1.SeenAt).ToArray();
            result.Add(contract, filtered);
        }
        wallet.Contracts = wallet.Contracts.OrderBy(c => c.CreatedAt).ToList();
        return (result, wallet.WalletDestination, wallet.Wallet);
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

    private TaskCompletionSource started = new();
    private ConcurrentDictionary<string, ECPrivKey> walletSigners = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        // load all wallets that have a private key as a signer
        var wallets = await dbContext.Wallets.Where(wallet => wallet.Wallet.StartsWith("nsec"))
            .Select(wallet => new {wallet.Id, wallet.Wallet}).ToListAsync(cancellationToken);
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
            walletSigners[id] = ArkExtensions.GetKeyFromWallet(wallet);

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


        await arkSubscriptionService.PollScripts(wallets.ToArray(), cancellationToken);
    }
}
