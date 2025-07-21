using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;

namespace NArk;


public class HashLockedArkPaymentContract: ArkContract
{

    
    
    public HashLockedArkPaymentContract(ECXOnlyPubKey server, Sequence exitDelay, ECXOnlyPubKey user, byte[] preimage, HashLockTypeOption hashLockType) : base(server)
    {
        HashLockType = hashLockType;
        ExitDelay = exitDelay;
        User = user;
        Preimage = preimage;
    }
    public HashLockTypeOption HashLockType { get; }
    public Sequence ExitDelay { get; }
    public ECXOnlyPubKey User { get; }
    public byte[] Preimage { get; }

    public byte[] Hash => HashLockType == HashLockTypeOption.RIPEMD160 ? Hashes.RIPEMD160(Preimage) : Hashes.SHA256(Preimage);

    public override string Type => ContractType;
    public const string ContractType = "HashLockPaymentContract";

    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["exit_delay"] = ExitDelay.Value.ToString(),
            ["user"] = User.ToHex(),
            ["preimage"] = Preimage.ToHex(),
            ["server"] = Server.ToHex(),
            ["hash_lock_type"] = Enum.GetName(HashLockType)
        };

        return data;
    }
    
    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            CreateClaimScript(),
            UnilateralPath()
        ];
    }

    public ScriptBuilder CreateClaimScript()
    {
        var hashLock = new HashLockTapScript(Hash, HashLockType);
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
        var hashLockType = Enum.Parse<HashLockTypeOption>(contractData["hash_lock_type"]);
        return new HashLockedArkPaymentContract(server, exitDelay, user, preimage, hashLockType );
        
    }
}