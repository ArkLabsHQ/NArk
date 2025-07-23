using NArk.Contracts;
using NArk.Services.Models;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NArk.Services;

/// <summary>
/// Core wallet operations for Ark
/// </summary>
public interface IWalletService
{
    // ECXOnlyPubKey GetXOnlyPubKeyFromWallet(string wallet);
    // string GetWalletId(ECXOnlyPubKey pubKey) => SHA256.HashData(pubKey.ToBytes()).ToHex();
    // string GetWalletId(string wallet) => GetWalletId(GetXOnlyPubKeyFromWallet(wallet));
    
    /// <summary>
    /// Derives a new payment contract for the wallet
    /// </summary>
    Task<ArkContract> DerivePaymentContractAsync(DeriveContractRequest request, CancellationToken cancellationToken = default);
    
    
    
}