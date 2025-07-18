using System.Collections.Concurrent;
using AsyncKeyedLock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.Extensions.Hosting;
using NArk;
using NArk.Services;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Services;





public class ArkWalletService(
    AsyncKeyedLocker asyncKeyedLocker,
    ArkPluginDbContextFactory dbContextFactory,
    ArkService.ArkServiceClient arkClient,
    ArkSubscriptionService arkSubscriptionService,
    IWalletService walletService,
    ILogger<ArkWalletService> logger):IHostedService, IArkadeMultiWalletSigner
{
    
    
    public async Task<ArkContract> DerivePaymentContract(string walletId, CancellationToken cancellationToken)
    {
        return (await DeriveNewContract(walletId, async wallet =>
        {
            var paymentContract = await walletService.DerivePaymentContractAsync(new DeriveContractRequest(wallet.PublicKey , RandomUtils.GetBytes(32)), cancellationToken);
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
        Func<ArkWallet, Task<(ArkWalletContract newContractData, ArkContract newContract)?>> setup, CancellationToken cancellationToken)
    {
        using var locker = await asyncKeyedLocker.LockAsync($"DeriveNewContract{walletId}", cancellationToken);
        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = await dbContext.Wallets.FindAsync([walletId],cancellationToken);
        if (wallet is null)
            throw new InvalidOperationException($"Wallet with ID {walletId} not found.");

        var contract = await setup(wallet);
        if (contract is null)
        {
            throw new InvalidOperationException($"Could not derive contract for wallet {walletId}");
        }

        await dbContext.WalletContracts.Upsert(contract.Value.Item1).RunAsync(cancellationToken);
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

    // public async Task<ArkWallet?> GetWalletAsync(string walletId, CancellationToken cancellationToken = default)
    // {
    //     await using var dbContext = dbContextFactory.CreateContext();
    //     return await dbContext.Wallets
    //         .Include(w => w.Contracts)
    //         .FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
    // }
    //
    // public async Task<List<ArkWallet>> GetAllWalletsAsync(CancellationToken cancellationToken = default)
    // {
    //     await using var dbContext = dbContextFactory.CreateContext();
    //     return await dbContext.Wallets
    //         .Include(w => w.Contracts)
    //         .ToListAsync(cancellationToken);
    // }

    public async Task<ArkWallet> Upsert(string wallet, CancellationToken cancellationToken = default)
    {
        
        
        var publicKey = ArkExtensions.GetXOnlyPubKeyFromWallet(wallet);

        await using var dbContext = dbContextFactory.CreateContext();
        
        
        var res = await dbContext.Wallets.Upsert(new ArkWallet()
        {
            Id = publicKey.ToHex(),
            Wallet = wallet,
        }).RunAndReturnAsync();
        LoadWalletSigner(publicKey.ToHex(), wallet);
        
      await   DeriveNewContract(publicKey.ToHex(), async wallet =>
      {
          var contract =
              await walletService.DerivePaymentContractAsync(new DeriveContractRequest(wallet.PublicKey),
                  cancellationToken);
          return (new ArkWalletContract
          {
              WalletId = publicKey.ToHex(),
              Active = true,
              ContractData = contract.GetContractData(),
              Script = contract.GetArkAddress().ScriptPubKey.ToHex(),
              Type = contract.Type,
          }, contract);
      }, cancellationToken);
      return res.Single();
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
        if (contract is  null)
        {
            return;
        }
        logger.LogInformation("Toggling contract {Script} ({active}) for wallet {WalletId}", script,active, detailsWalletId);

        contract.Active = active;
        if (await dbContext.SaveChangesAsync() > 0)
        {
            await  arkSubscriptionService.UpdateManualSubscriptionAsync(script, active, CancellationToken.None);
        }
        

    }

    public async Task<Dictionary<ArkWalletContract, VTXO[]>?> GetWalletInfo(string walletId)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = await dbContext.Wallets.Include(w => w.Contracts).FirstOrDefaultAsync(w => w.Id == walletId);
        if (wallet is null)
        {
            return null;
        }
        var contractScripts = wallet.Contracts.Select(c => c.Script).ToArray();
        var vtxos = await dbContext.Vtxos.Where(vtxo1 => contractScripts.Contains(vtxo1.Script)).ToListAsync();
        
        var result = new Dictionary<ArkWalletContract, VTXO[]>();
        foreach (var contract in wallet.Contracts)
        {
            var filtered = vtxos.Where(vtxo1 => vtxo1.Script == contract.Script).ToArray();
            result.Add(contract, filtered);
        }
        return result;

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
        var wallets = await dbContext.Wallets.Where(wallet => wallet.Wallet.StartsWith("nsec")).Select(wallet => new  {wallet.Id, wallet.Wallet}).ToListAsync(cancellationToken);
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
        return Task.FromResult<IArkadeWalletSigner>(new WalletSigner(walletSigners[walletId]));
        
    }
    
    public class WalletSigner : IArkadeWalletSigner
    {
        private readonly ECPrivKey _key;

        public WalletSigner(ECPrivKey key)
        {
            _key = key;
        }

        public Task<ECXOnlyPubKey> GetPublicKey(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_key.CreateXOnlyPubKey());
        }

        public Task<(SecpSchnorrSignature, ECXOnlyPubKey)> Sign(uint256 data, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((_key.SignBIP340(data.ToBytes())
                , _key.CreateXOnlyPubKey()));
        }
    }
}
