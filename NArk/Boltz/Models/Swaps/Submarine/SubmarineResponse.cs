using System.Text.Json.Serialization;
using NArk.Boltz.Models.Swaps.Reverse;

namespace NArk.Boltz.Models.Swaps.Submarine
{
    public class SubmarineResponse
    {
        [JsonPropertyName("id")] public string Id { get; set; }

        [JsonPropertyName("address")] public string Address { get; set; }

        [JsonPropertyName("expectedAmount")] public long ExpectedAmount { get; set; }

        [JsonPropertyName("claimPublicKey")] public string ClaimPublicKey { get; set; } 

        [JsonPropertyName("acceptZeroConf")] public bool AcceptZeroConf { get; set; }

        [JsonPropertyName("timeoutBlockHeights")]
        public TimeoutBlockHeights TimeoutBlockHeights { get; set; }
    }
}
