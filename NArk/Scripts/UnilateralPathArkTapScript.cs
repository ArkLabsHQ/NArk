using NBitcoin;

namespace NArk;

public class UnilateralPathArkTapScript: ScriptBuilder
{
    public Sequence Timeout { get; }
    public ScriptBuilder? Condition { get; }

    public UnilateralPathArkTapScript(Sequence timeout, ScriptBuilder? condition= null)
    {
        Timeout = timeout;
        Condition = condition;
    }

    public static TapScript Create(Sequence timeout, ScriptBuilder? condition = null) => 
        new UnilateralPathArkTapScript(timeout, condition).Build();

    public override IEnumerable<Op> BuildScript()
    {
        var condition = Condition?.BuildScript()?.ToList() ?? [];
        if (condition.Count > 0)
        {
            condition.Add(OpcodeType.OP_VERIFY);
        }
        
        return [Op.GetPushOp(Timeout.Value), OpcodeType.OP_CHECKSEQUENCEVERIFY,OpcodeType.OP_DROP];
    }
}