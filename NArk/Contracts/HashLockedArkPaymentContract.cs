using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;

namespace NArk;

public class HashLockedArkPaymentContract: ArkContract
{
    public HashLockedArkPaymentContract(ECXOnlyPubKey server, Sequence exitDelay, ECXOnlyPubKey user, byte[] preimage) : base(server)
    {
        ExitDelay = exitDelay;
        User = user;
        Preimage = preimage;
    }
    public Sequence ExitDelay { get; }
    public ECXOnlyPubKey User { get; }
    public byte[] Preimage { get; }
    public uint160 Hash => Hashes.Hash160(Preimage);
    public override string Type => ContractType;
    public const string ContractType = "HashLockPaymentContract";
    
    // public override TaprootSpendInfo GetTaprootSpendInfo()
    // {
    //     var sortedScriptBuilders = GetScriptBuilders().Select(scriptBuilder => scriptBuilder.Build()).Order( TaprootConstants.TapScriptComparer).ToArray();
    //     
    //     // (uint, TapScript)[] scriptWeghts = [(0,CreateClaimScript().Build()),(0,CollaborativePath().Build()),(0,UnilateralPath().Build())];
    //     var builder = TaprootBuilder.WithHuffmanTree(sortedScriptBuilders.Select(x => ((uint)0, x)).ToArray());
    //     return builder.Finalize( new TaprootInternalPubKey(TaprootConstants.UnspendableKey.ToECXOnlyPubKey().ToBytes()));
    // }

    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["exit_delay"] = ExitDelay.Value.ToString(),
            ["user"] = User.ToHex(),
            ["preimage"] = Preimage.ToHex(),
            ["server"] = Server.ToHex()
        };

        return data;
    }
    
    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            CollaborativePath(),
            CreateClaimScript(),
            UnilateralPath()
        ];
    }

    public ScriptBuilder CollaborativePath()
    {
        return new CollaborativePathArkTapScript(Server, new NofNMultisigTapScript([User]));
    }
    public ScriptBuilder CreateClaimScript()
    {
        var hashLock = new HashLockTapScript(Hash);
        var receiverMultisig = new NofNMultisigTapScript([User]);
        return new CollaborativePathArkTapScript(Server,
            new CompositeTapScript(hashLock, new VerifyTapScript() ,receiverMultisig));
    }
    
    public ScriptBuilder UnilateralPath()
    {
        var ownerScript = new NofNMultisigTapScript( [User]);
        return new UnilateralPathArkTapScript(ExitDelay, ownerScript);
    }
    
    public static  ArkContract? Parse(Dictionary<string, string> contractData)
    {
        var server = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["server"]));
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var user = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["user"]));
        var preimage = Convert.FromHexString(contractData["preimage"]);
        return new HashLockedArkPaymentContract(server, exitDelay, user, preimage );
        
    }
}