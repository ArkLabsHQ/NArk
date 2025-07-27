using NArk.Contracts;
using NArk.Services;
using NBitcoin;

namespace NArk;

public class SpendableArkCoinWithSigner : ArkCoin
{
    public IArkadeWalletSigner Signer { get; }
    public LockTime? SpendingLockTime { get; }
    public Sequence? SpendingSequence { get; }
    public TapScript SpendingScript { get; set; }
    public WitScript? SpendingConditionWitness { get; set; }
    

    public SpendableArkCoinWithSigner(
        ArkContract contract, 
        OutPoint outpoint, 
        TxOut txout,
        IArkadeWalletSigner signer,
        TapScript spendingScript,
        WitScript? spendingConditionWitness,
        LockTime? lockTime,
        Sequence? sequence
        ) : base(contract, outpoint, txout)
    {
        Signer = signer;
        SpendingScript = spendingScript;
        SpendingConditionWitness = spendingConditionWitness;
        SpendingLockTime = lockTime;
        SpendingSequence = sequence;
        if (sequence is null &&
            spendingScript.Script.ToOps() is { } ops && ops.Contains(OpcodeType.OP_CHECKSEQUENCEVERIFY))
        {
            throw new InvalidOperationException("Sequence is required");
        }
        
    }
}