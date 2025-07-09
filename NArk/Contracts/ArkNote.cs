using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;

namespace NArk;

public class ArkNoteContract: ArkContract
{
    public byte[] Preimage { get; }
    public byte[] Hash => Hashes.SHA256(Preimage);
    
    public OutPoint Outpoint => new OutPoint(new uint256(Hash), 0);

    public ArkNoteContract(byte[] preimage) : base(null)
    {
        Preimage = preimage;
    }

    public override string Type => ContractType;
    public static string ContractType = "arknote";

    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        yield return new ArkNoteScriptBuilder(Hash);
    }

    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>();
        data["preimage"] = Encoders.Hex.EncodeData(Preimage);
        return data;
    }

    public static ArkContract Parse(Dictionary<string, string> arg)
    {
        var preimage = Encoders.Hex.DecodeData(arg["preimage"]);
        return new ArkNoteContract(preimage);
    }
}

public class ArkNoteScriptBuilder: ScriptBuilder
{
    public byte[] Hash { get; }

    public ArkNoteScriptBuilder(byte[] hash)
    {
        Hash = hash;
    }

    public override IEnumerable<Op> BuildScript()
    {
        yield return OpcodeType.OP_SHA256;
       yield return Op.GetPushOp(Hash);
        yield return OpcodeType.OP_EQUAL;
    }
}
