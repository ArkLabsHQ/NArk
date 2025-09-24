using NBitcoin;

namespace NArk.Scripts;

public class HashLockTapScript(byte[] hash, HashLockTypeOption hashLockType) : ScriptBuilder
{
    public byte[] Hash { get; } = hash;
    public HashLockTypeOption HashLockType { get; } = hashLockType;

    public HashLockTapScript(uint160 hash): this(hash.ToBytes(false), HashLockTypeOption.HASH160) { }
    public HashLockTapScript(uint256 hash): this(hash.ToBytes(false), HashLockTypeOption.SHA256) { }

    public override IEnumerable<Op> BuildScript()
    {
        if (HashLockType == HashLockTypeOption.HASH160)
        {
            yield return OpcodeType.OP_HASH160;
        }
        else
        {
            yield return OpcodeType.OP_SHA256;
        }
        yield return Op.GetPushOp(Hash);
        yield return OpcodeType.OP_EQUAL;
    }
}