using NBitcoin;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public interface IArkadeWalletSigner
{
    Task<bool> CanHandle(string walletId, CancellationToken cancellationToken = default);

    Task<SecpSchnorrSignature> Sign(string walletId, uint256 data, byte[]? tweak = null,
        CancellationToken cancellationToken = default);
}

