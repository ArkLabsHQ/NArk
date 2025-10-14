using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Contracts;
using NArk.Models;
using NArk.Scripts;
using NArk.Services;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeSpender(
    AsyncKeyedLocker asyncKeyedLocker,
    ArkadeWalletSignerProvider arkadeWalletSignerProvider,
    ArkTransactionBuilder arkTransactionBuilder,
    ArkService.ArkServiceClient arkServiceClient,
    ArkWalletService arkWalletService,
    ILogger<ArkadeSpender> logger,
    IOperatorTermsService operatorTermsService,
    ArkVtxoSynchronizationService arkVtxoSynchronizationService,
    BitcoinTimeChainProvider bitcoinTimeChainProvider)
{
    public async Task<uint256> Spend(string walletId, TxOut[] outputs, CancellationToken cancellationToken = default)
    {
        var coinSet = await GetSpendableCoins([walletId],false, cancellationToken);

        if (!coinSet.TryGetValue(walletId, out var coins) || coins.Count == 0)
        {
            throw new InvalidOperationException($"No coins to spend for wallet {walletId}");
        }

        logger.LogInformation($"Found {coins.Count} VTXOs to spend for wallet {walletId}");
        var wallet = await arkWalletService.GetWallet(walletId, cancellationToken);
        return await Spend(wallet, coins, outputs, cancellationToken);
    }

    public async Task<uint256> Spend(ArkWallet wallet, IEnumerable<SpendableArkCoinWithSigner> coins, TxOut[] outputs,
        CancellationToken cancellationToken = default)
    {
        using var l = await asyncKeyedLocker.LockAsync($"ark-{wallet.Id}-txs-spending", cancellationToken);

        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        var destination = await GetDestination(wallet, operatorTerms);
        return await SpendWalletCoins(coins, operatorTerms, outputs, destination, cancellationToken);
    }

    private async Task<uint256> SpendWalletCoins(IEnumerable<SpendableArkCoinWithSigner> coins,
        ArkOperatorTerms operatorTerms, TxOut[] outputs, ArkAddress changeAddress, CancellationToken cancellationToken)
    {
        var totalInput = coins.Sum(x => x.TxOut.Value);
        var totalOutput = outputs.Sum(x => x.Value);

        if (totalInput < totalOutput)
            throw new InvalidOperationException(
                $"Insufficient funds. Available: {totalInput}, Required: {totalOutput}");

        var change = totalInput - totalOutput;
        if (change > operatorTerms.Dust)
            outputs = outputs.Concat([new TxOut(Money.Satoshis(change), changeAddress)]).ToArray();

        try
        {
            return await arkTransactionBuilder.ConstructAndSubmitArkTransaction(
                coins,
                outputs,
                arkServiceClient,
                cancellationToken);
        }
        catch (Exception ex)
        {
            var scripts = coins.Select(x => x.Contract.GetArkAddress().ScriptPubKey.ToHex())
                .Concat(
                    outputs.Select(y => y.ScriptPubKey.ToHex())).ToHashSet();

            await arkVtxoSynchronizationService.PollScriptsForVtxos(scripts.ToHashSet(), cancellationToken);
            throw;
        }
    }

    public async Task<Dictionary<string, List<SpendableArkCoinWithSigner>>> GetSpendableCoins(string[]? walletIds, bool includeRecoverable,
        CancellationToken cancellationToken)
    {
        return await GetSpendableCoins(walletIds, null, includeRecoverable, cancellationToken);
    }

    /// <summary>
    /// Get spendable coins for specified wallets, optionally filtered by specific VTXO outpoints
    /// </summary>
    /// <param name="vtxoOutpoints">Optional set of VTXO outpoints to filter by. If null, returns all spendable coins.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<Dictionary<string, List<SpendableArkCoinWithSigner>>> GetSpendableCoins(
        string[]? walletIds,
        HashSet<OutPoint>? vtxoOutpoints,
        bool includeRecoverable,
        CancellationToken cancellationToken)
    {
        // Filter VTXOs at database level for efficiency
        var vtxosAndContracts = await arkWalletService.GetVTXOsAndContracts(walletIds, false, includeRecoverable, vtxoOutpoints,null,null, cancellationToken);

        walletIds = vtxosAndContracts.Select(grouping => grouping.Key).ToArray();
        var signers = await arkadeWalletSignerProvider.GetSigners(walletIds, cancellationToken);

        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        var res = new Dictionary<string, List<SpendableArkCoinWithSigner>>();
        foreach (var walletSigner in signers)
        {
            var walletId = walletSigner.Key;
            var signer = walletSigner.Value;
            if (!vtxosAndContracts.TryGetValue(walletId, out var group))
                continue;
            // No need to filter again - already filtered at DB level
            var coins = await GetSpendableCoins(group, signer, operatorTerms, false, null, cancellationToken);
            res.Add(walletId, coins);
        }
        
        return res;
    }

    private async Task<List<SpendableArkCoinWithSigner>> GetSpendableCoins(
        Dictionary<ArkWalletContract, VTXO[]> group, IArkadeWalletSigner signer,
        ArkOperatorTerms operatorTerms, bool includeRecoverable, HashSet<OutPoint>? vtxoOutpoints = null, CancellationToken cancellationToken = default)
    {
        var coins = new List<SpendableArkCoinWithSigner>();

        foreach (var contractData in group)
        {
            var contract = ArkContract.Parse(contractData.Key.Type, contractData.Key.ContractData);
            if (contract is null)
                continue;
            foreach (var vtxo in contractData.Value)
            {
                if (vtxo.SpentByTransactionId is not null)
                    continue;
                if (!includeRecoverable && vtxo.Recoverable)
                    continue;

                // Filter by specific VTXO outpoints if provided
                if (vtxoOutpoints != null)
                {
                    var vtxoOutpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex);
                    if (!vtxoOutpoints.Contains(vtxoOutpoint))
                    {
                        continue;
                    }
                }

                if (!operatorTerms.SignerKey.ToBytes().SequenceEqual(contract.Server.ToBytes()))
                {
                    continue;
                }

                var res = await GetSpendableCoin(signer, contract, vtxo.ToCoin(), vtxo.Recoverable, vtxo.ExpiresAt, cancellationToken);
                if (res is not null)
                    coins.Add(res);
            }
        }
        
        return coins;
    }

    private async Task<SpendableArkCoinWithSigner?> GetSpendableCoin(IArkadeWalletSigner signer,
        ArkContract contract, ICoinable vtxo, bool recoverable, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var user = await signer.GetXOnlyPublicKey(cancellationToken);
        switch (contract)
        {
            case ArkPaymentContract arkPaymentContract:
                if (arkPaymentContract.User.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract, vtxo, signer, arkPaymentContract.CollaborativePath(), null, null, null, recoverable, expiresAt);
                }

                break;
            case HashLockedArkPaymentContract hashLockedArkPaymentContract:
                if (hashLockedArkPaymentContract.User!.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract, vtxo, signer,
                        hashLockedArkPaymentContract.CreateClaimScript(),
                        new WitScript(Op.GetPushOp(hashLockedArkPaymentContract.Preimage)), null, null, recoverable,expiresAt);
                }

                break;
            case VHTLCContract htlc:
                if (htlc.Preimage is not null && htlc.Receiver.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract, vtxo, signer,
                        htlc.CreateClaimScript(),
                        new WitScript(Op.GetPushOp(htlc.Preimage!)), null, null, recoverable, expiresAt);
                }
                
                var (timestamp, height) = await bitcoinTimeChainProvider.Get(cancellationToken);

                switch (htlc.RefundLocktime.IsTimeLock)
                {
                    case true:
                        if (htlc.RefundLocktime.Date <= Utils.UnixTimeToDateTime(timestamp) && htlc.Sender.ToBytes().SequenceEqual(user.ToBytes()))
                        {
                            return ToArkCoin(contract, vtxo, signer,
                                htlc.CreateRefundWithoutReceiverScript(),
                                null, htlc.RefundLocktime, null, recoverable, expiresAt);
                        }
                        break;
                    case false:
                        if (htlc.RefundLocktime.Height <= height && htlc.Sender.ToBytes().SequenceEqual(user.ToBytes()))
                        {
                            return ToArkCoin(contract, vtxo, signer,
                                htlc.CreateRefundWithoutReceiverScript(),
                                null, htlc.RefundLocktime, null, recoverable, expiresAt);
                        }
                        break;
                }
                break;
        }

        return null;
    }

    private static SpendableArkCoinWithSigner ToArkCoin(ArkContract c, ICoinable vtxo, IArkadeWalletSigner signer,
        ScriptBuilder leaf, WitScript? witness, LockTime? lockTime, Sequence? sequence, bool recoverable, DateTimeOffset expiry )
    {
        return new SpendableArkCoinWithSigner(c, expiry, vtxo.Outpoint, vtxo.TxOut, signer, leaf, witness, lockTime, sequence, recoverable);
    }

    public Task<ArkAddress> GetDestination(ArkWallet wallet, ArkOperatorTerms arkOperatorTerms)
    {
        var destination = wallet.Destination;
        destination ??= 
            ContractUtils
                .DerivePaymentContract(new DeriveContractRequest(arkOperatorTerms, wallet.PublicKey))
                .GetArkAddress();
        return Task.FromResult(destination);
    }
}