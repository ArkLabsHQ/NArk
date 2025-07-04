using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk;

public class ArkPaymentContract : ArkContract
{
    
    
    public Sequence ExitDelay { get; }
    public virtual ECXOnlyPubKey User { get; }
    

    public ArkPaymentContract(ECXOnlyPubKey server, Sequence exitDelay, ECXOnlyPubKey user) 
        : base(server)
    {
        ExitDelay = exitDelay;
        User = user;
    }

    public override string Type => ContractType;
    public const string ContractType = "Payment";
    

    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            CollaborativePath(),
            UnilateralPath()
        ];
    }

    public ScriptBuilder CollaborativePath()
    {
        var ownerScript = new NofNMultisigTapScript( [User]);
        return new CollaborativePathArkTapScript(Server, ownerScript);
    }
    
    
    public WitScript CollaborativePathWitness(SecpSchnorrSignature user )
    {
        var tapLeaf = CollaborativePath().Build();
        
        
        return new WitScript(
            Op.GetPushOp(user.ToBytes()), 
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }
    
    public ScriptBuilder UnilateralPath()
    {
        var ownerScript = new NofNMultisigTapScript( [User]);
        return new UnilateralPathArkTapScript(ExitDelay, ownerScript);
    }
    
    public WitScript UnilateralPathWitness(SecpSchnorrSignature user )
    {
        var tapLeaf = UnilateralPath().Build();
        
        
        return new WitScript(
            Op.GetPushOp(user.ToBytes()), 
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }

    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>();
        data["exit_delay"] = ExitDelay.Value.ToString();
        data["user"] = Convert.ToHexString(User.ToBytes());
        data["server"] = Convert.ToHexString(Server.ToBytes()); 
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