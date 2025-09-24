using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Services;

public class MemoryWalletSigner : IArkadeWalletSigner
{
    private readonly ECPrivKey _key;

    public MemoryWalletSigner(ECPrivKey key)
    {
        _key = key;
    }
    public async Task<ECXOnlyPubKey> GetPublicKey(CancellationToken cancellationToken = default)
    {
        return _key.CreateXOnlyPubKey();
    }

    public async Task<(SecpSchnorrSignature, ECXOnlyPubKey)> Sign(uint256 data, CancellationToken cancellationToken = default)
    {
        var sig = _key.SignBIP340(data.ToBytes());
        return (sig, _key.CreateXOnlyPubKey());
    }
}