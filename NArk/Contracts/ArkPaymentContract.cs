using NArk.Extensions;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Contracts;

public class ArkPaymentContract(ECXOnlyPubKey server, Sequence exitDelay, ECXOnlyPubKey user) : ArkContract(server)
{
    public override string Type => ContractType;
    public const string ContractType = "Payment";
    
    public ECXOnlyPubKey User => user;

    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            CollaborativePath(),
            UnilateralPath()
        ];
    }

    public ScriptBuilder CollaborativePath()
    {
        var ownerScript = new NofNMultisigTapScript([user]);
        return new CollaborativePathArkTapScript(Server!, ownerScript);
    }
    
    public ScriptBuilder UnilateralPath()
    {
        var ownerScript = new NofNMultisigTapScript([user]);
        return new UnilateralPathArkTapScript(exitDelay, ownerScript);
    }
    
    public WitScript UnilateralPathWitness(SecpSchnorrSignature server,SecpSchnorrSignature user )
    {
        var tapLeaf = UnilateralPath().Build();
        
        
        return new WitScript(
            Op.GetPushOp(server.ToBytes()),
            Op.GetPushOp(user.ToBytes()), 
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }

    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["exit_delay"] = exitDelay.Value.ToString(),
            ["user"] = user.ToHex(),
            ["server"] = Server!.ToHex()
        };
        return data;
    }
    
    public static  ArkContract? Parse(Dictionary<string, string> contractData)
    {
        var server = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["server"]));
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var user = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["user"]));
        return new ArkPaymentContract(server, exitDelay, user);
        
    }
}