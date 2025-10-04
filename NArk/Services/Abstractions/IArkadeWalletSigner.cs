using NBitcoin;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Services.Abstractions;

public interface IArkadeWalletSigner
{
    Task<ECXOnlyPubKey> GetXOnlyPublicKey(CancellationToken cancellationToken = default);
    Task<ECPubKey> GetPublicKey(CancellationToken cancellationToken = default);
    
    Task<(SecpSchnorrSignature, ECXOnlyPubKey)> Sign(uint256 data, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Sign using MUSIG2 protocol with a nonce and context
    /// </summary>
    Task<MusigPartialSignature> SignMusig(MusigContext context, MusigPrivNonce nonce, CancellationToken cancellationToken = default);
}
