using NBitcoin;

namespace NArk.Scripts;

public class CompositeTapScript(params ScriptBuilder[] scripts) : ScriptBuilder
{
    public ScriptBuilder[] Scripts => scripts;

    public static TapScript Create(params ScriptBuilder[] scripts) => new CompositeTapScript(scripts).Build();

    public override IEnumerable<Op> BuildScript()
    {
        return Scripts.SelectMany(script => script.BuildScript());
    }
}