using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk;

public class TweakedArkPaymentContract : ArkPaymentContract
{
    public byte[] Tweak { get; }

    public TweakedArkPaymentContract(ECXOnlyPubKey server, Sequence exitDelay, ECXOnlyPubKey user, byte[] tweak) 
        : base(server, exitDelay, user)
    {
        Tweak = tweak;
        OriginalUser = user;
    }

    public ECXOnlyPubKey OriginalUser { get; }

    public override ECXOnlyPubKey User => OriginalUser.AddTweak(Tweak).ToXOnlyPubKey();
    
    public override string Type => ContractType;

    public const string ContractType = "TweakedPayment";
    
    
    
    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>();
        data["exit_delay"] = ExitDelay.Value.ToString();
        data["user"] = OriginalUser.ToHex();
        data["tweak"] = Tweak.ToHex();
        data["server"] = Server.ToHex();
        
        return data;
    }
    
    public static ArkContract? Parse(Dictionary<string, string> contractData)
    {
        var server = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["server"]));
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var user = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["user"]));
        var tweak = Convert.FromHexString(contractData["tweak"]);
        return new TweakedArkPaymentContract(server, exitDelay, user, tweak);
        
    }
}