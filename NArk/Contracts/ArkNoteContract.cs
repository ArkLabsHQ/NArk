using NArk.Scripts;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace NArk.Contracts;

public class ArkNoteContract: HashLockedArkPaymentContract
{

    public OutPoint Outpoint => new OutPoint(new uint256(Hash), 0);

    public ArkNoteContract(byte[] preimage) : base(null, new Sequence(), null,preimage, HashLockTypeOption.SHA256)
    {
    }

    public override string Type => ContractType;
    public new static string ContractType = "arknote";

    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        yield return new HashLockTapScript(Hash, HashLockTypeOption.SHA256);
    }

    public override TapScript[] GetTapScriptList()
    {
        //we override to remove the checks.
        var leaves = GetScriptBuilders().ToArray();
        return leaves.Select(x => x.Build()).ToArray();
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