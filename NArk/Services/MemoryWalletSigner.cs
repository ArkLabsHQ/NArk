using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Services;

public class MemoryWalletSigner : IArkadeWalletSigner
{
    private readonly ECPrivKey _key;

    public MemoryWalletSigner(ECPrivKey key)
    {
        _key = key;
    }
    public async Task<ECXOnlyPubKey> GetXOnlyPublicKey(CancellationToken cancellationToken = default)
    {
        return _key.CreateXOnlyPubKey();
    }

    public async Task<ECPubKey> GetPublicKey(CancellationToken cancellationToken = default)
    {
        return _key.CreatePubKey();
    }

    public async Task<(SecpSchnorrSignature, ECXOnlyPubKey)> Sign(uint256 data, CancellationToken cancellationToken = default)
    {
        var sig = _key.SignBIP340(data.ToBytes());
        return (sig, _key.CreateXOnlyPubKey());
    }

    public async Task<MusigPartialSignature> SignMusig(MusigContext context, MusigPrivNonce nonce, CancellationToken cancellationToken = default)
    {
        // Create MUSIG2 partial signature using the private key and nonce
        var partialSig = context.Sign(_key, nonce);
        return partialSig;
    }
}