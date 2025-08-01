using NBitcoin;

namespace NArk.Services;

public class IntentTxOut:TxOut
{
    public enum IntentOutputType
    {
        VTXO,
        OnChain
    }
    public IntentOutputType Type { get; set; }
}