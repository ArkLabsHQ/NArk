using NBitcoin;

namespace NArk.Scripts;

public abstract class ScriptBuilder
{

    public abstract IEnumerable<Op> BuildScript();
    
    public virtual TapScript Build()
    {
        return new TapScript(new Script(BuildScript()), TapLeafVersion.C0);
    }
}

