using NBitcoin;

namespace NArk;

public class HashLockTapScript : ScriptBuilder
{
    public uint160 Hash { get; }

    public HashLockTapScript(uint160 hash)
    {
        Hash = hash;
    }

    public static TapScript Create(uint160 hash) => new HashLockTapScript(hash).Build();

    public override IEnumerable<Op> BuildScript()
    {
        yield return OpcodeType.OP_HASH160;
        yield return Op.GetPushOp(Hash.ToBytes());
        yield return OpcodeType.OP_EQUAL;
    }
}