using NArk.Contracts;
using NBitcoin;

namespace NArk;

public class ArkCoin : Coin
{
    public ArkCoin(ArkContract contract, OutPoint outpoint, TxOut txout) : base(outpoint, txout)
    {
        Contract = contract;
    }
    public ArkContract Contract { get; set; }
}