namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class VersionResponse
{
    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("commitHash")]
    public string CommitHash { get; set; }
}
