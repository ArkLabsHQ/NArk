using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Submarine;

public class ChannelCreationInfo
{
    [JsonPropertyName("auto")]
    public bool Auto { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("inboundLiquidity")]
    public long? InboundLiquidity { get; set; }
}