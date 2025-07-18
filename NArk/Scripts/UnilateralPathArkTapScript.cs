using NBitcoin;

namespace NArk;

public class UnilateralPathArkTapScript: ScriptBuilder
{
    public Sequence Timeout { get; }
    public NofNMultisigTapScript Owners { get; }
    public ScriptBuilder? Condition { get; set; }

    public UnilateralPathArkTapScript(Sequence timeout, NofNMultisigTapScript owners, ScriptBuilder? condition= null)
    {
        Timeout = timeout;
        Owners = owners;
        Condition = condition;
    }


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