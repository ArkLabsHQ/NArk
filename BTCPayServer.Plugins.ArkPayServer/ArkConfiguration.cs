using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkConfiguration
{
    public ArkConfiguration(string arkUri,
        string? boltzUri)
    {
        ArkUri = arkUri;
        BoltzUri = boltzUri;
    }

    public ArkConfiguration()
    {
        
    }

    [JsonPropertyName("ark")]
    public string ArkUri { get; init; }
    [JsonPropertyName("boltz")]
    public string? BoltzUri { get; init; }

    public void Deconstruct(out string ArkUri, out string? BoltzUri)
    {
        ArkUri = this.ArkUri;
        BoltzUri = this.BoltzUri;
    }
}