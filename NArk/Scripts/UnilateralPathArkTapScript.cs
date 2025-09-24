using NBitcoin;

namespace NArk.Scripts;

public class UnilateralPathArkTapScript(Sequence timeout, NofNMultisigTapScript owners, ScriptBuilder? condition = null) : ScriptBuilder
{
    public Sequence Timeout => timeout;
    public NofNMultisigTapScript Owners => owners;
    public ScriptBuilder? Condition => condition;

    public override IEnumerable<Op> BuildScript()
    {
        var condition = Condition?.BuildScript().ToList() ?? [];
        if (condition.Count > 0)
        {
            condition.Add(OpcodeType.OP_VERIFY);
        }
        var owners = Owners.BuildScript().ToList();
        owners[^1] = OpcodeType.OP_CHECKSIG;
        return [ ..condition, Op.GetPushOp(Timeout.Value), OpcodeType.OP_CHECKSEQUENCEVERIFY,OpcodeType.OP_DROP, ..owners ];
    }
}