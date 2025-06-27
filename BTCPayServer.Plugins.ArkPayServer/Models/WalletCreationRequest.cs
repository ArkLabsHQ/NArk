using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class WalletCreationRequest
{
    public PubKey PubKey { get; }

    public WalletCreationRequest(string pubKey)
    {
        if (string.IsNullOrWhiteSpace(pubKey))
        {
            throw new ArgumentException("pubKey cannot be empty", nameof(pubKey));
        }

        PubKey = new PubKey(pubKey);
    }
}