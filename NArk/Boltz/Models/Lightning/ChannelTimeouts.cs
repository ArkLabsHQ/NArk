using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Lightning;

public class ChannelTimeouts
{
    [JsonPropertyName("csvTimeout")]
    public int CsvTimeout { get; set; }
}