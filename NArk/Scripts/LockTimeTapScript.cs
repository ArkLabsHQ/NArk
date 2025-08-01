﻿using NBitcoin;

namespace NArk.Scripts;

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
        yield return Op.GetPushOp(LockTime.Value);
        yield return OpcodeType.OP_CHECKLOCKTIMEVERIFY;
        yield return OpcodeType.OP_DROP;
    }
}