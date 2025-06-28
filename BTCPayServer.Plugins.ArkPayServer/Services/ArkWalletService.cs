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
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkWalletService(
    AsyncKeyedLocker asyncKeyedLocker,
    BTCPayNetworkProvider btcPayNetworkProvider,
    BTCPayNetworkProvider networkProvider,
    ArkPluginDbContextFactory dbContextFactory,
    ArkService.ArkServiceClient arkClient,
    ArkSubscriptionService arkSubscriptionService,
    ArkOperatorTermsService arkOperatorTermsService,
    ILogger<ArkWalletService> logger)
{
    private readonly DerivationSchemeParser _derivationSchemeParser =
        btcPayNetworkProvider.BTC.GetDerivationSchemeParser();

    public async Task<ArkContract> DerivePaymentContract(Guid walletId, CancellationToken cancellationToken)
    {
        return await DeriveNewContract(walletId, async (ArkWallet wallet) =>
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
            var tweak = RandomUtils.GetBytes(32);
            if (tweak is null)
            {
                throw new Exception("Could not derive preimage randomly");
            }


            var encoder = Bech32Encoder.ExtractEncoderFromString(wallet.Wallet);
            encoder.StrictLength = false;
            encoder.SquashBytes = true;
            ECXOnlyPubKey? pubKey = null;
            var keyData = encoder.DecodeDataRaw(wallet.Wallet, out _);
            switch (Encoding.UTF8.GetString(encoder.HumanReadablePart))
            {
                case "nsec":
                    pubKey = ECPrivKey.Create(keyData).CreateXOnlyPubKey();
                    break;
                case "npub":
                    pubKey = ECXOnlyPubKey.Create(keyData);
                    break;
            }


            var operatorTerms = await arkOperatorTermsService.GetOperatorTerms(cancellationToken);
            var paymentContract =
                new TweakedArkPaymentContract(operatorTerms.SignerKey, operatorTerms.UnilateralExit, pubKey, tweak);

            var contract = new ArkWalletContract
            {
                WalletId = wallet.Id,
                Active = true,
                ContractData = paymentContract.GetContractData()
            };

            return (contract, paymentContract);
        }, cancellationToken);
    }

    public async Task<ArkContract?> DeriveNewContract(Guid walletId,
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

    public async Task<ArkWallet> CreateNewWalletAsync(WalletCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating new Ark wallet");

        try
        {
            var arkWallet = new ArkWallet
            {
                Id = Guid.NewGuid(),
                PubKey = request.PubKey.ToHex(),
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
            logger.LogError("Failed to create boarding address due to unique constraint violation: {Error}",
                ex.Message);
            throw new InvalidOperationException(
                "A boarding address with this address already exists. Please try again.");
        }
    }

    /// <summary>
    /// Gets all boarding addresses for a wallet
    /// </summary>
    public async Task<List<BoardingAddress>> GetBoardingAddressesAsync(Guid walletId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        return await dbContext.BoardingAddresses
            .Where(b => b.WalletId == walletId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);
    }
}