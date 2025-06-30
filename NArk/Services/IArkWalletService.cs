using NArk.Services.Models;
using NBitcoin.Secp256k1;

namespace NArk.Services;

/// <summary>
/// Core wallet operations for Ark
/// </summary>
public interface IArkWalletService
{
    ECXOnlyPubKey GetXOnlyPubKeyFromWallet(string wallet);
    
    /// <summary>
    /// Derives a new payment contract for the wallet
    /// </summary>
    Task<ArkContract> DerivePaymentContractAsync(DeriveContractRequest request, CancellationToken cancellationToken = default);
}