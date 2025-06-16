using System.ComponentModel.DataAnnotations.Schema;

namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkWalletContract
{
    public string Script { get; set; }
    public string DescriptorTemplate { get; set; }
    public bool Active { get; set; }
    public string Type { get; set; }
    
    [Column(TypeName = "jsonb")]
    public Dictionary<string, string> ContractData { get; set; }
    
}