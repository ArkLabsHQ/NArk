using NArk.Extensions;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;

namespace NArk.Contracts;

public class HashLockedArkPaymentContract(ECXOnlyPubKey? server, Sequence exitDelay, ECXOnlyPubKey? user, byte[] preimage, HashLockTypeOption hashLockType) : ArkContract(server)
{
    public byte[] Hash
    {
        get
        {
            return hashLockType switch
            {
                HashLockTypeOption.HASH160 => Hashes.Hash160(preimage).ToBytes(),
                HashLockTypeOption.SHA256 => Hashes.SHA256(preimage),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }

    public override string Type => ContractType;
    public const string ContractType = "HashLockPaymentContract";
    public ECXOnlyPubKey? User => user;
    public byte[] Preimage => preimage;
    public Sequence ExitDelay => exitDelay;
    public HashLockTypeOption HashLockType => hashLockType;

    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["exit_delay"] = exitDelay.Value.ToString(),
            ["user"] = user?.ToHex() ?? throw new InvalidOperationException("User is required for contract data generation"),
            ["preimage"] = preimage.ToHex(),
            ["server"] = Server?.ToHex() ?? throw new InvalidOperationException("Server key is required for contract data generation"),
            ["hash_lock_type"] = Enum.GetName(hashLockType) ?? throw new ArgumentOutOfRangeException(nameof(hashLockType), "Invalid hash lock type")
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
        var hashLock = new HashLockTapScript(Hash, hashLockType);
        var receiverMultisig = new NofNMultisigTapScript([user ?? throw new InvalidOperationException("User is required for claim script generation")]);
        return new CollaborativePathArkTapScript(Server,
            new CompositeTapScript(hashLock, new VerifyTapScript() ,receiverMultisig));
    }
    
    public ScriptBuilder UnilateralPath()
    {
        var ownerScript = new NofNMultisigTapScript([user ?? throw new InvalidOperationException("User is required for unilateral script generation")]);
        return new UnilateralPathArkTapScript(exitDelay, ownerScript);
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