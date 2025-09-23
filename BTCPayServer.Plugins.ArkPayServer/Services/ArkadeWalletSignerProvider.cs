using NArk.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeWalletSignerProvider
{
    private readonly IEnumerable<IArkadeMultiWalletSigner> _walletSigners;

    public ArkadeWalletSignerProvider(IEnumerable<IArkadeMultiWalletSigner> walletSigners)
    {
        _walletSigners = walletSigners;
    }

    public async Task<IArkadeWalletSigner?> GetSigner(string walletId, CancellationToken cancellationToken = default)
    {
        var signers = await GetSigners([walletId],cancellationToken);
        return signers.TryGetValue(walletId, out var signer) ? signer : null;
    }

    public async Task<Dictionary<string, IArkadeWalletSigner>> GetSigners(string[] walletId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IArkadeWalletSigner>();
        foreach (var signer in _walletSigners)
        {
            foreach (var id in walletId)
            {
                if (await signer.CanHandle(id, cancellationToken))
                {
                    result.Add(id, await signer.CreateSigner(id, cancellationToken));
                }
            }
        }
        return result;
        
    }
}