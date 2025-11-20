using NArk.Contracts;
using NBitcoin;

namespace NArk;

public class ArkCoin : Coin
{
    public ArkCoin(ArkContract contract, OutPoint outpoint, TxOut txout, DateTimeOffset? expiresAt, uint? expiresAtHeight) : base(outpoint, txout)
    {
        Contract = contract;
        ExpiresAt = expiresAt;
        ExpiresAtHeight = expiresAtHeight;
    }
    public ArkContract Contract { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
    
    public uint? ExpiresAtHeight { get; set; }

    public override string ToString()
    {
        return $"{Outpoint} - {TxOut.ScriptPubKey.ToHex()} -{TxOut.Value}";
    }
}

