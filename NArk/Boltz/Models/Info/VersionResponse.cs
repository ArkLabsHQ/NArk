using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Info;

public class VersionResponse
{
    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("commitHash")]
    public string CommitHash { get; set; }
}
