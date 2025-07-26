using System.Text.Json.Serialization;
using NBitcoin;

namespace BTCPayServer.Data;

public class ArkPayoutProof:IPayoutProof
{
    [JsonConverter(typeof(NBitcoin.JsonConverters.UInt256JsonConverter))]
    public uint256 TransactionId { get; set; }
       
    [JsonIgnore]
    public string Id { get { return TransactionId?.ToString(); } }

    public string ProofType => Type;


    public const string Type = "PayoutProofArk";

    public string Link => null;

}