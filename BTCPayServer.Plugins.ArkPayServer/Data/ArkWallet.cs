namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkWallet
{
    
    public string DescriptorTemplate { get; set; }
    public uint CurrentIndex { get; set; }

    public List<ArkWalletContract> Contracts { get; set; } = new List<ArkWalletContract>();


}