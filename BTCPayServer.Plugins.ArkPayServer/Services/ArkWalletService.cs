using AsyncKeyedLock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Ark.V1;

using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkWalletService(
    AsyncKeyedLocker asyncKeyedLocker,
    BTCPayNetworkProvider networkProvider,
    ArkPluginDbContextFactory dbContextFactory,
    ArkService.ArkServiceClient arkClient,
    ILogger<ArkWalletService> logger)
{
    public async Task DeriveNewContract(
        Guid walletId,
        Func<ArkWallet, Task<ArkWalletContract?>> setup,
        CancellationToken cancellationToken)
    {
        using var locker = await asyncKeyedLocker.LockAsync($"DeriveNewContract{walletId}", cancellationToken);
        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = await dbContext.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
        if (wallet is null)
            throw new InvalidOperationException($"Wallet with ID {walletId} not found.");

        var contract = await setup(wallet);
        if (contract is null)
            return;

        await dbContext.WalletContracts.AddAsync(contract, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("New contract derived for wallet {WalletId}: {Script}", walletId, contract.Script);
    }

    public async Task<ArkWallet> CreateNewWalletAsync(WalletCreationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Password.Length < 8)
        {
            throw new ArgumentException("Password must be at least 8 characters long", nameof(request.Password));
        }

        logger.LogInformation("Creating new Ark wallet");

        try
        {
            var extPrivateKey = GeneratePrivateKey(request.Mnemonic, request.Passphrase);
            
            var passwordBytes = System.Text.Encoding.UTF8.GetBytes(request.Password);
            var passwordHash = CryptoUtils.HashPassword(passwordBytes);
            var xPub = extPrivateKey.Neuter();
            var privateKeyBytes = extPrivateKey.ToBytes();
            var encryptedPrivateKey = CryptoUtils.EncryptAES256(privateKeyBytes, passwordBytes);

            var publicKeyHex = xPub.ToString(networkProvider.BTC.NBitcoinNetwork);

            var arkWallet = new ArkWallet
            {
                Id = Guid.NewGuid(),
                PubKey = publicKeyHex,
                EncryptedPrvkey = encryptedPrivateKey,
                PasswordHash = passwordHash,
                CurrentIndex = 0
            };

            await using var dbContext = dbContextFactory.CreateContext();
            
            await dbContext.Wallets.AddAsync(arkWallet, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            
            logger.LogInformation("Successfully created and stored new Ark wallet with ID {WalletId}", arkWallet.Id);
            
            return arkWallet;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message?.Contains("UNIQUE constraint failed") == true)
        {
            throw new InvalidOperationException("A wallet with this public key already exists. Please use a different seed.");
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            logger.LogError(ex, "Unexpected error occurred while creating wallet");
            throw;
        }
    }

    private static ExtKey GeneratePrivateKey(string? mnemonic, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            return new ExtKey();
        }
        
        try
        {
            var mnemonicObj = new Mnemonic(mnemonic);
            var extKey = mnemonicObj.DeriveExtKey(passphrase);
            return extKey;
        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid mnemonic format.", nameof(mnemonic), ex);
        }
    }

    public async Task<ArkWallet?> GetWalletAsync(Guid walletId, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Creates a new boarding address for the specified wallet using the Ark operator's GetBoardingAddress gRPC call
    /// </summary>
    /// <param name="walletId">The wallet ID to create the boarding address for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created boarding address information</returns>
    public async Task<BoardingAddress> DeriveNewBoardingAddress(
        Guid walletId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        
        var wallet = await dbContext.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
        if (wallet is null)
        {
            throw new InvalidOperationException($"Wallet with ID {walletId} not found.");
        }
        
        var latestBoardingAddress = await dbContext.BoardingAddresses.Where(w => w.WalletId == walletId)
            .OrderByDescending(w => w.DerivationIndex)
            .FirstOrDefaultAsync(cancellationToken);
        
        var newDerivationIndex = latestBoardingAddress is null ? 0 : latestBoardingAddress.DerivationIndex + 1;
        
        var xPub = ExtPubKey.Parse(wallet.PubKey, networkProvider.BTC.NBitcoinNetwork);
        
        // TODO: We should probably pick some more deliberate derivation path
        var derivedPubKey = xPub.Derive(newDerivationIndex).PubKey.ToHex();

        var response = await arkClient.GetBoardingAddressAsync(new GetBoardingAddressRequest
        {
            Pubkey = derivedPubKey
        }, cancellationToken: cancellationToken);
        
        // Get operator info for additional metadata
        var operatorInfo = await arkClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
        
        try
        {
            var boardingAddressEntity = new BoardingAddress
            {
                OnchainAddress = response.Address,
                WalletId = walletId,
                DerivationIndex = newDerivationIndex,
                BoardingExitDelay = (uint)operatorInfo.BoardingExitDelay,
                ContractData = response.HasDescriptor_ ? response.Descriptor_ : response.Tapscripts?.ToString() ?? "",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await dbContext.BoardingAddresses.AddAsync(boardingAddressEntity, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            
            logger.LogInformation("New boarding address created for wallet {WalletId}: {Address}", 
                walletId, response.Address);
            
            return boardingAddressEntity;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message?.Contains("UNIQUE constraint failed") == true || 
                                          ex.InnerException?.Message?.Contains("duplicate key") == true)
        {
            logger.LogError("Failed to create boarding address due to unique constraint violation: {Error}", ex.Message);
            throw new InvalidOperationException("A boarding address with this address already exists. Please try again.");
        }
    }

    /// <summary>
    /// Gets all boarding addresses for a wallet
    /// </summary>
    public async Task<List<BoardingAddress>> GetBoardingAddressesAsync(Guid walletId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        return await dbContext.BoardingAddresses
            .Where(b => b.WalletId == walletId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);
    }

}
