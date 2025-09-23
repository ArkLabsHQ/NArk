using System.Text.Json.Serialization;
using BTCPayServer.Data;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

public class ArkPayoutProof: IPayoutProof
{
    public const string Type = "PayoutProofArk";
    
    [JsonConverter(typeof(NBitcoin.JsonConverters.UInt256JsonConverter))]
    public uint256 TransactionId { get; set; }
    
    [JsonIgnore]
    public string Id => TransactionId.ToString();
    
    public string ProofType => Type;
    
    public string Link => null!; // FIXME
}