using System.Collections.Generic;
using System.Globalization;
using NBitcoin;
using Newtonsoft.Json;

namespace BTCPayServer.Data
{
    public class PayoutTransactionArkBlob : IPayoutProof
    {
        [JsonConverter(typeof(NBitcoin.JsonConverters.UInt256JsonConverter))]
        public uint256 TransactionId { get; set; }

        public string ProofType { get; } = Type;
        public const string Type = "PayoutTransactionArkBlob";

        [JsonIgnore]
        public string Link => null;

        public bool? Accounted { get; set; } = true;
        [JsonIgnore]
        public string Id { get { return TransactionId?.ToString(); } }
    }
}
