﻿using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Contracts;
using NArk.Services;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeSpender
{
    private readonly AsyncKeyedLocker _asyncKeyedLocker;
    private readonly ArkTransactionBuilder _arkTransactionBuilder;
    private readonly ArkService.ArkServiceClient _arkServiceClient;
    private readonly ArkPluginDbContextFactory _arkPluginDbContextFactory;
    private readonly ArkadeWalletSignerProvider _walletSignerProvider;
    private readonly ILogger<ArkadeSpender> _logger;
    private readonly IOperatorTermsService _operatorTermsService;
    private readonly ArkSubscriptionService _arkSubscriptionService;

    public ArkadeSpender(AsyncKeyedLocker asyncKeyedLocker, 
        ArkPluginDbContextFactory arkPluginDbContextFactory,
        ArkadeWalletSignerProvider arkadeWalletSignerProvider,
        ArkTransactionBuilder arkTransactionBuilder,
        ArkService.ArkServiceClient arkServiceClient,
        ILogger<ArkadeSpender> logger,
        IOperatorTermsService operatorTermsService,
        ArkSubscriptionService arkSubscriptionService)
    {
        _asyncKeyedLocker = asyncKeyedLocker;
        _arkPluginDbContextFactory = arkPluginDbContextFactory;
        _walletSignerProvider = arkadeWalletSignerProvider;
        _arkTransactionBuilder = arkTransactionBuilder;
        _arkServiceClient = arkServiceClient;
        _logger = logger;
        _operatorTermsService = operatorTermsService;
        _arkSubscriptionService = arkSubscriptionService;
    }

    public async Task Spend(string walletId, TxOut[] outputs, CancellationToken cancellationToken = default)
    {
        using var l = await _asyncKeyedLocker.LockAsync($"ark-{walletId}-txs-spending", cancellationToken);
        
        var operatorTerms = await _operatorTermsService.GetOperatorTerms(cancellationToken);
        await using var db = _arkPluginDbContextFactory.CreateContext();
        
        var wallet = await db.Wallets.FirstOrDefaultAsync(x => x.Id == walletId, cancellationToken);
        if (wallet is null)
            throw new InvalidOperationException($"Wallet {walletId} not found");
            
        var signer = await _walletSignerProvider.GetSigner(walletId, cancellationToken);
        if (signer is null)
            throw new InvalidOperationException($"Signer for wallet {walletId} not found");

        string[] allowedContractTypes = {VHTLCContract.ContractType, HashLockedArkPaymentContract.ContractType, ArkPaymentContract.ContractType};
        
        var vtxosAndContracts = await db.Vtxos
            .Where(vtxo => vtxo.SpentByTransactionId == null && !vtxo.IsNote) // VTXO is unspent
            .Join(
                db.WalletContracts.Where(c => allowedContractTypes.Contains(c.Type) && c.WalletId == walletId),
                vtxo => vtxo.Script, // Key from VTXO
                contract => contract.Script, // Key from WalletContract
                (vtxo, contract) => new {Vtxo = vtxo, Contract = contract} // Select both VTXO and contract
            )
            .ToListAsync(cancellationToken);

        if (vtxosAndContracts.Count == 0)
        {
            _logger.LogWarning($"No spendable VTXOs found for wallet {walletId}");
            return;
        }

        _logger.LogInformation($"Found {vtxosAndContracts.Count} VTXOs to spend for wallet {walletId}");
        
        await SpendWalletCoins(wallet, vtxosAndContracts.Select(x => (x.Vtxo, x.Contract)).ToArray(), signer, operatorTerms, outputs, cancellationToken);
    }
    
    public async Task SweepWalletWithLock(ArkWallet wallet, (VTXO Vtxo, ArkWalletContract Contract)[] vtxosAndContracts, IArkadeWalletSigner signer, CancellationToken cancellationToken = default)
    {
        var operatorTerms = await _operatorTermsService.GetOperatorTerms(cancellationToken);
        
        if (vtxosAndContracts.Length == 0)
        {
            _logger.LogInformation($"No VTXOs to sweep for wallet {wallet.Id}");
            return;
        }

        
        await SweepWalletCoins(wallet, vtxosAndContracts, signer, operatorTerms, cancellationToken);
    }

    private static SpendableArkCoinWithSigner ToArkCoin(ArkContract c, ICoinable vtxo, IArkadeWalletSigner signer,
        TapScript leaf, WitScript? witness, LockTime? lockTime, Sequence? sequence)
    {
        return new SpendableArkCoinWithSigner(c, vtxo.Outpoint, vtxo.TxOut, signer, leaf, witness, lockTime, sequence);
    }

    private async Task SpendWalletCoins(ArkWallet wallet, (VTXO Vtxo, ArkWalletContract Contract)[] group, IArkadeWalletSigner signer, ArkOperatorTerms operatorTerms, TxOut[] outputs, CancellationToken cancellationToken)
    {
        var publicKey = wallet.PublicKey;
        var coins = await GetSpendableCoins(group, signer, publicKey, operatorTerms);
        
        if (coins.Count == 0)
        {
            _logger.LogInformation($"No spendable coins found for wallet {wallet.Id}");
            return;
        }

        var totalInput = coins.Sum(x => x.TxOut.Value);
        var totalOutput = outputs.Sum(x => x.Value);
        
        if (totalInput < totalOutput)
            throw new InvalidOperationException($"Insufficient funds. Available: {totalInput}, Required: {totalOutput}");

        var destination = wallet.Destination;
        if (destination is null)
        {
            var destinationContract = ContractUtils.DerivePaymentContract(new DeriveContractRequest(operatorTerms, publicKey));
            destination = destinationContract.GetArkAddress();
        }
        
        var change = totalInput - totalOutput;
        if (change > operatorTerms.Dust)
            outputs = outputs.Concat([new TxOut(Money.Satoshis(change), destination)]).ToArray();
        
        try
        {
            await _arkTransactionBuilder.ConstructAndSubmitArkTransaction(
                coins,
                outputs,
                _arkServiceClient,
                cancellationToken);
        }
        catch (Exception ex)
        {
            var scripts = group.Select(x => x.Contract.Script)
                .Concat(
                    outputs.Select(y => y.ScriptPubKey.ToHex())).ToHashSet();
            
            await _arkSubscriptionService.PollScripts(scripts.ToArray(), cancellationToken);
            throw;
        }
        
    }

    private async Task SweepWalletCoins(ArkWallet wallet, (VTXO Vtxo, ArkWalletContract Contract)[] group, IArkadeWalletSigner signer, ArkOperatorTerms operatorTerms, CancellationToken cancellationToken)
    {
        var publicKey = wallet.PublicKey;
        var coins = await GetSpendableCoins(group, signer, publicKey, operatorTerms);

        if (coins.Count == 0)
            return;

        var destination = wallet.Destination;
        if (destination is null)
        {
            var destinationContract = ContractUtils.DerivePaymentContract(new DeriveContractRequest(operatorTerms, publicKey));
            destination = destinationContract.GetArkAddress();
        }
        
        // Only sweep if we have coins not at the destination to avoid infinite sweeping loops
        if (coins.All(x => x.TxOut.IsTo(destination)))
        {
            _logger.LogDebug($"Skipping sweep for wallet {wallet.Id}: all coins are already at destination");
            return;
        }

        var sum = coins.Sum(x => x.TxOut.Value);
        if (sum == 0)
            return;
        
        
        
        _logger.LogInformation($"Found {coins.Count} VTXOs to sweep for wallet {wallet.Id}");
        // Use the consolidated spend method for sweeping
        var sweepOutput = new TxOut(sum, destination);
        await SpendWalletCoins(wallet, group, signer, operatorTerms, [sweepOutput], cancellationToken);
    }

    private async Task<List<SpendableArkCoinWithSigner>> GetSpendableCoins(
        (VTXO Vtxo, ArkWalletContract Contract)[] group, IArkadeWalletSigner signer, ECXOnlyPubKey user,
        ArkOperatorTerms operatorTerms)
    {
        var coins = new List<SpendableArkCoinWithSigner>();
        
        foreach (var vtxo in group)
        {
            if(vtxo.Vtxo.IsNote || vtxo.Vtxo.SpentByTransactionId is not null)
                continue;
            var contract = ArkContract.Parse(vtxo.Contract.Type, vtxo.Contract.ContractData);
            if (contract is null)
                continue;

            if (!operatorTerms.SignerKey.ToBytes().SequenceEqual(contract.Server.ToBytes()))
            {
                continue;
            }
            
            var res= await GetSpendableCoin(signer, contract, vtxo.Vtxo.ToCoin());
            if (res is not null)
                coins.Add(res);
        }
        
        return coins;
    }

    public static async Task<SpendableArkCoinWithSigner?> GetSpendableCoin(IArkadeWalletSigner signer, ArkContract contract, ICoinable vtxo)
    {
        ECXOnlyPubKey user = await signer.GetPublicKey();
        switch (contract)
        {
            case ArkPaymentContract arkPaymentContract:
                if (arkPaymentContract.User.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract,vtxo, signer,arkPaymentContract.CollaborativePath().Build(),null, null, null);
                }
                break;
            case HashLockedArkPaymentContract hashLockedArkPaymentContract:
                if (hashLockedArkPaymentContract.User.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract,vtxo, signer,
                        hashLockedArkPaymentContract.CreateClaimScript().Build(),
                        new WitScript(Op.GetPushOp(hashLockedArkPaymentContract.Preimage)), null, null);
                }
                break;
            case VHTLCContract htlc:
                if (htlc.Preimage is not null && htlc.Receiver.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract,vtxo, signer,
                        htlc.CreateClaimScript().Build(),
                        new WitScript(Op.GetPushOp(htlc.Preimage!)), null, null);
                }

                if (htlc.RefundLocktime.IsTimeLock &&
                    htlc.RefundLocktime.Date < DateTime.UtcNow && htlc.Sender.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract,vtxo, signer,
                        htlc.CreateRefundWithoutReceiverScript().Build(),
                        null, htlc.RefundLocktime, null);
                }

                break;
        }
        return null;
    }
}