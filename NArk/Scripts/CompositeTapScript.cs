using NBitcoin;

namespace NArk;

public class CompositeTapScript : ScriptBuilder
{
    public ScriptBuilder[] Scripts { get; }

    public CompositeTapScript(params ScriptBuilder[] scripts)
    {
        Scripts = scripts;
    }

    public static TapScript Create(params ScriptBuilder[] scripts) => new CompositeTapScript(scripts).Build();

    public override IEnumerable<Op> BuildScript()
    {
        return Scripts.SelectMany(script => script.BuildScript());
    }
}