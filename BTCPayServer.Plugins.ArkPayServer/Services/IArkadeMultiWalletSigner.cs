using NArk.Services.Abstractions;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public interface IArkadeMultiWalletSigner
{
    Task<bool> CanHandle(string walletId, CancellationToken cancellationToken = default);
    
    public Task<IArkadeWalletSigner> CreateSigner(string walletId, CancellationToken cancellationToken = default);
}