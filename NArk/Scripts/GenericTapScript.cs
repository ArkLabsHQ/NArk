using NBitcoin;

namespace NArk.Scripts;

public class GenericTapScript(TapScript script) : ScriptBuilder
{
    public override IEnumerable<Op> BuildScript()
    {
        return script.Script.ToOps();
    }
}