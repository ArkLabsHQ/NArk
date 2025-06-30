using System.Text;
using AsyncKeyedLock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Ark.V1;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Models;
using NArk;
using NArk.Services;
using NArk.Services.Models;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkWalletService(
    AsyncKeyedLocker asyncKeyedLocker,
    BTCPayNetworkProvider btcPayNetworkProvider,
    BTCPayNetworkProvider networkProvider,
    ArkPluginDbContextFactory dbContextFactory,
    ArkService.ArkServiceClient arkClient,
    ArkSubscriptionService arkSubscriptionService,
    IArkWalletService arkWalletService,
    ILogger<ArkWalletService> logger)
{
    private readonly DerivationSchemeParser _derivationSchemeParser =
        btcPayNetworkProvider.BTC.GetDerivationSchemeParser();
    
    
    public async Task<ArkContract> DerivePaymentContract(string walletId, CancellationToken cancellationToken)
    {
        return (await DeriveNewContract(walletId, async wallet =>
        {
            // var newIndex = wallet.CurrentIndex + 1;
            // wallet.CurrentIndex = newIndex;
            // var descriptor = _derivationSchemeParser.ParseOD(wallet.DescriptorTemplate);
            //
            // var derivation = descriptor.AccountDerivation.GetDerivation(newIndex);
            //
            // var xOnlyKey = ECXOnlyPubKey.Create(derivation.ScriptPubKey.ToBytes().AsSpan()[2..]);
            // ECPubKey xx;
            // ECXOnlyPubKey xxx;
            // xxx.A

            // var descriptor = Miniscript.Parse(wallet.DescriptorTemplate,
            //     new MiniscriptParsingSettings(_network, KeyType.Taproot));
            // var derivation = descriptor.Derive(AddressIntent.Deposit, (int)newIndex);
            // var key = derivation.DerivedKeys.First().Key.Key.GetPublicKey().TaprootInternalKey;
            // var xOnlyKey = ECXOnlyPubKey.Create(key.ToBytes());
            var paymentContract = await arkWalletService.DerivePaymentContractAsync(new DeriveContractRequest(wallet.Wallet), cancellationToken);
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

    public async Task<ArkWallet> CreateNewWalletAsync(string wallet,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating new Ark wallet");

        var key = arkWalletService.GetXOnlyPubKeyFromWallet(wallet);
        
        try
        {
            var arkWallet = new ArkWallet
            {
                Id = SHA256.HashData(key.ToBytes()).ToHex(),
                Wallet = wallet
            };

            await using var dbContext = dbContextFactory.CreateContext();

            await dbContext.Wallets.AddAsync(arkWallet, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Successfully created and stored new Ark wallet with ID {WalletId}", arkWallet.Id);

            return arkWallet;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message?.Contains("UNIQUE constraint failed") == true)
        {
            throw new InvalidOperationException(
                "A wallet with this public key already exists. Please use a different seed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error occurred while creating wallet");
            throw;
        }
    }

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
}