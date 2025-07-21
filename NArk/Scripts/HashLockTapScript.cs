using NBitcoin;

namespace NArk;
public enum HashLockTypeOption
{
    RIPEMD160,
    SHA256,
}

public class HashLockTapScript : ScriptBuilder
{
    public byte[] Hash { get; }
    public HashLockTypeOption HashLockType { get; }

    public HashLockTapScript(uint160 hash): this(hash.ToBytes(), HashLockTypeOption.RIPEMD160)
    {
        
    }
    public HashLockTapScript(uint256 hash): this(hash.ToBytes(), HashLockTypeOption.SHA256)
    {
        
    }
    
    public HashLockTapScript(byte[] hash, HashLockTypeOption hashLockType)
    {
        Hash = hash;
        HashLockType = hashLockType;
    }

    public override IEnumerable<Op> BuildScript()
    {
        if (HashLockType == HashLockTypeOption.RIPEMD160)
        {
            yield return OpcodeType.OP_RIPEMD160;
        }
        else
        {
            yield return OpcodeType.OP_SHA256;
        }
        yield return Op.GetPushOp(Hash);
        yield return OpcodeType.OP_EQUAL;
    }
}