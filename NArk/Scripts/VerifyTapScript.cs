using NBitcoin;

namespace NArk;

public class VerifyTapScript : ScriptBuilder
{


    public static TapScript Create() => new VerifyTapScript().Build();

    public override IEnumerable<Op> BuildScript()
    {
        yield return OpcodeType.OP_VERIFY;
    }
}