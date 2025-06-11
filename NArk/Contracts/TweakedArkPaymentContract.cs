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
    
    public override string Type => "TweakedPayment";
    
    protected override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>();
        data["exit_delay"] = ExitDelay.Value.ToString();
        data["user"] = Convert.ToHexString(OriginalUser.ToBytes());
        data["tweak"] = Convert.ToHexString(Tweak);
        data["server"] = Convert.ToHexString(Server.ToBytes()); 
        
        return data;
    }
    
    public override ArkContract? Parse(Dictionary<string, string> contractData)
    {
        var server = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["server"]));
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var user = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["user"]));
        var tweak = Convert.FromHexString(contractData["tweak"]);
        return new TweakedArkPaymentContract(server, exitDelay, user, tweak);
        
    }
}