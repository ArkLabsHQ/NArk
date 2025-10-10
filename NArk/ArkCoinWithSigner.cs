using NArk.Contracts;
using NArk.Scripts;
using NArk.Services;
using NArk.Services.Abstractions;
using NBitcoin;

namespace NArk;

public class SpendableArkCoin : ArkCoin
{
    public LockTime? SpendingLockTime { get; }
    public Sequence? SpendingSequence { get; }
    public ScriptBuilder SpendingScriptBuilder { get; set; }
    public TapScript SpendingScript => SpendingScriptBuilder.Build();
    public WitScript? SpendingConditionWitness { get; set; }

    public bool Recoverable { get; set; }

    public SpendableArkCoin(ArkContract contract,
        DateTimeOffset expiresAt,
        OutPoint outpoint,
        TxOut txout,
        ScriptBuilder spendingScriptBuilder,
        WitScript? spendingConditionWitness,
        LockTime? lockTime,
        Sequence? sequence, bool recoverable) : base(contract, outpoint, txout, expiresAt)
    {
        SpendingScriptBuilder = spendingScriptBuilder;
        SpendingConditionWitness = spendingConditionWitness;
        SpendingLockTime = lockTime;
        SpendingSequence = sequence;
        Recoverable = recoverable;


        if (sequence is null && spendingScriptBuilder.BuildScript().Contains(OpcodeType.OP_CHECKSEQUENCEVERIFY))
        {
            throw new InvalidOperationException("Sequence is required");
        }
    }


  

    public PSBTInput? FillPSBTInput(PSBT psbt)
    {
        var psbtInput = psbt.Inputs.FindIndexedInput(Outpoint);
        if (psbtInput is null)
        {
            return null;
        }

        psbtInput.SetArkFieldTapTree(Contract.GetTapScriptList());
        psbtInput.SetTaprootLeafScript(Contract.GetTaprootSpendInfo(), SpendingScript);
        if (SpendingConditionWitness is not null)
        {
            psbtInput.SetArkFieldConditionWitness(SpendingConditionWitness);
        }

        return psbtInput;
    }
}

public class SpendableArkCoinWithSigner : SpendableArkCoin
{
    public IArkadeWalletSigner Signer { get; }


    public SpendableArkCoinWithSigner(ArkContract contract,
        DateTimeOffset expiresAt,
        OutPoint outpoint,
        TxOut txout,
        IArkadeWalletSigner signer,
        ScriptBuilder spendingScriptBuilder,
        WitScript? spendingConditionWitness,
        LockTime? lockTime,
        Sequence? sequence, bool recoverable) : base(contract, expiresAt, outpoint, txout, spendingScriptBuilder,
        spendingConditionWitness, lockTime, sequence, recoverable)
    {
        Signer = signer;
    }


    public async Task SignAndFillPSBT(
        PSBT psbt,
        TaprootReadyPrecomputedTransactionData precomputedTransactionData,
        CancellationToken cancellationToken)
    {
        var psbtInput = FillPSBTInput(psbt);
        if (psbtInput is null)
        {
            return;
        }

        var gtx = psbt.GetGlobalTransaction();
        var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
            new TaprootExecutionData((int) psbtInput.Index, SpendingScript.LeafHash));
        var (sig, ourKey) = await Signer.Sign(hash, cancellationToken);

        psbtInput.SetTaprootScriptSpendSignature(ourKey, SpendingScript.LeafHash, sig);
    }
}