using NArk.Contracts;
using NArk.Scripts;
using NArk.Services;
using NArk.Services.Abstractions;
using NBitcoin;

namespace NArk;

public class SpendableArkCoinWithSigner : ArkCoin
{
    public IArkadeWalletSigner Signer { get; }
    public LockTime? SpendingLockTime { get; }
    public Sequence? SpendingSequence { get; }
    public ScriptBuilder SpendingScriptBuilder { get; set; }
    public TapScript SpendingScript  => SpendingScriptBuilder.Build();
    public WitScript? SpendingConditionWitness { get; set; }
    

    public SpendableArkCoinWithSigner(
        ArkContract contract, 
        OutPoint outpoint, 
        TxOut txout,
        IArkadeWalletSigner signer,
        ScriptBuilder spendingScriptBuilder,
        WitScript? spendingConditionWitness,
        LockTime? lockTime,
        Sequence? sequence
        ) : base(contract, outpoint, txout)
    {
        Signer = signer;
        SpendingScriptBuilder = spendingScriptBuilder;
        SpendingConditionWitness = spendingConditionWitness;
        SpendingLockTime = lockTime;
        SpendingSequence = sequence;
        if (sequence is null && spendingScriptBuilder.BuildScript().Contains(OpcodeType.OP_CHECKSEQUENCEVERIFY))
        {
            throw new InvalidOperationException("Sequence is required");
        }
        
    }

    public async Task SignAndFillPSBT(
        PSBT psbt, 
        TaprootReadyPrecomputedTransactionData precomputedTransactionData,
        CancellationToken cancellationToken)
    {
        var psbtInput = psbt.Inputs.FindIndexedInput(Outpoint);
        if (psbtInput is null)
        {
            return;
        }
        
        psbtInput.Unknown.SetArkField(Contract.GetTapScriptList());
        psbtInput.SetTaprootLeafScript(Contract.GetTaprootSpendInfo(), SpendingScript);

        
        var gtx = psbt.GetGlobalTransaction();
        var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
            new TaprootExecutionData((int)psbtInput.Index, SpendingScript.LeafHash));
        var (sig, ourKey) = await Signer.Sign(hash,  cancellationToken);
        
        psbtInput.SetTaprootScriptSpendSignature(ourKey, SpendingScript.LeafHash, sig);
        if (SpendingConditionWitness is not null)
        {
            psbtInput.Unknown.SetArkField(SpendingConditionWitness);
        }

    }
}