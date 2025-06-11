using NBitcoin;

namespace NArk;

public abstract class ScriptBuilder
{

    public abstract IEnumerable<Op> BuildScript();
    
    public virtual TapScript Build()
    {
        return new TapScript(new Script(BuildScript()), TapLeafVersion.C0);
    }
}

