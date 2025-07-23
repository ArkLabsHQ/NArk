using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Scripts;




public class CollaborativePathArkTapScript:ScriptBuilder
{
    public ECXOnlyPubKey Server { get; }
    public ScriptBuilder? Condition { get; }

    public CollaborativePathArkTapScript(ECXOnlyPubKey server, ScriptBuilder? condition = null)
    {
        Server = server;
        Condition = condition;
    }

    public static TapScript Create(ECXOnlyPubKey server, ScriptBuilder? condition = null) => 
        new CollaborativePathArkTapScript(server, condition).Build();

    public override IEnumerable<Op> BuildScript()
    {
        var condition = Condition?.BuildScript()?.ToList() ?? [];
        foreach (var op in condition)
        {
            yield return op;
        }
        yield return Op.GetPushOp(Server.ToBytes());
        yield return OpcodeType.OP_CHECKSIG;
    }
}