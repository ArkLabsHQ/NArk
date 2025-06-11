using NBitcoin;

namespace NArk;

public class LockTimeTapScript : ScriptBuilder
{
    public LockTime LockTime { get; }

    public LockTimeTapScript(LockTime lockTime)
    {
        LockTime = lockTime;
    }

    public static TapScript Create(LockTime lockTime) => new LockTimeTapScript(lockTime).Build();

    public override IEnumerable<Op> BuildScript()
    {
        yield return Op.GetPushOp(LockTime.ToBytes());
        yield return OpcodeType.OP_CHECKLOCKTIMEVERIFY;
        yield return OpcodeType.OP_DROP;
    }
}