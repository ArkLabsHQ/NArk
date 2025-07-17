using NBitcoin;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public interface IArkadeWalletSigner
{
    Task<ECXOnlyPubKey> GetPublicKey(CancellationToken cancellationToken = default);
    Task<(SecpSchnorrSignature, ECXOnlyPubKey)> Sign( uint256 data, byte[]? tweak = null,
        CancellationToken cancellationToken = default);
}
