using AsyncKeyedLock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;

using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkWalletService(
    AsyncKeyedLocker asyncKeyedLocker,
    ArkPluginDbContextFactory dbContextFactory,
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
            throw new ArgumentException("Password must be at least 8 characters long", nameof(request.Password));

        logger.LogInformation("Creating new Ark wallet");

        try
        {
            var privateKey = GeneratePrivateKey(request.Mnemonic, request.Passphrase);
            
            var passwordBytes = System.Text.Encoding.UTF8.GetBytes(request.Password);
            var passwordHash = CryptoUtils.HashPassword(passwordBytes);
            var publicKey = privateKey.PubKey;
            var privateKeyBytes = privateKey.ToBytes();
            var encryptedPrivateKey = CryptoUtils.EncryptAES256(privateKeyBytes, passwordBytes);
            
            var publicKeyHex = publicKey.ToHex();

            var arkWallet = new ArkWallet
            {
                Id = Guid.NewGuid(),
                PubKey = publicKeyHex,
                EncryptedPrvkey = encryptedPrivateKey,
                PasswordHash = passwordHash,
                CurrentIndex = 0
            };

            await using var dbContext = dbContextFactory.CreateContext();
            
            try
            {
                await dbContext.Wallets.AddAsync(arkWallet, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                
                logger.LogInformation("Successfully created and stored new Ark wallet with ID {WalletId}", arkWallet.Id);
                
                return arkWallet;
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message?.Contains("UNIQUE constraint failed") == true)
            {
                throw new InvalidOperationException("A wallet with this public key already exists. Please use a different seed.");
            }
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            logger.LogError(ex, "Unexpected error occurred while creating wallet");
            throw;
        }
    }

    private Key GeneratePrivateKey(string? mnemonic, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            var privateKey = new Key();
            logger.LogInformation("Generated random private key for new wallet");
            return privateKey;
        }
        else
        {
            try
            {
                var mnemonicObj = new Mnemonic(mnemonic);
                var extKey = mnemonicObj.DeriveExtKey(passphrase);
                var privateKey = extKey.PrivateKey;
                logger.LogInformation("Created private key from provided mnemonic");
                return privateKey;
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Invalid mnemonic format.", nameof(mnemonic), ex);
            }
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

}
