using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk;

public class CollaborativePathArkTapScript : NofNMultisigTapScript
{
    public ECXOnlyPubKey Server => Owners[0];
    public ScriptBuilder? Condition { get; }

    public CollaborativePathArkTapScript(ECXOnlyPubKey server, ScriptBuilder? condition = null) : base([server])
    {
        Condition = condition;
    }

    public static TapScript Create(ECXOnlyPubKey server, ScriptBuilder? condition = null) => 
        new CollaborativePathArkTapScript(server, condition).Build();

    public override IEnumerable<Op> BuildScript()
    {
        return [..Condition?.BuildScript() ?? new List<Op>(), ..base.BuildScript()];
    }
}