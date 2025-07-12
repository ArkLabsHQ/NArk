using BTCPayServer.Plugins.ArkPayServer.Services;
using NBitcoin;

namespace NArk;

public class ArkCoinWithSigner : ArkCoin
{
    public IArkadeWalletSigner Signer { get; }

    public ArkCoinWithSigner(IArkadeWalletSigner signer, ArkContract contract, OutPoint outpoint, TxOut txout) : base(contract, outpoint, txout)
    {
        Signer = signer;
    }
}