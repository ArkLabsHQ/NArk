using NBitcoin;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public interface IArkadeMultiWalletSigner
{
    Task<bool> CanHandle(string walletId, CancellationToken cancellationToken = default);
    
    public Task<IArkadeWalletSigner> CreateSigner(string walletId, CancellationToken cancellationToken = default);
}



public interface IArkadeWalletSigner
{
    Task<SecpSchnorrSignature> Sign( uint256 data, byte[]? tweak = null,
        CancellationToken cancellationToken = default);
}
