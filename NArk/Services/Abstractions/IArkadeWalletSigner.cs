using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Services.Abstractions;

public interface IArkadeWalletSigner
{
    Task<ECXOnlyPubKey> GetPublicKey(CancellationToken cancellationToken = default);
    Task<(SecpSchnorrSignature, ECXOnlyPubKey)> Sign( uint256 data,CancellationToken cancellationToken = default);
}
