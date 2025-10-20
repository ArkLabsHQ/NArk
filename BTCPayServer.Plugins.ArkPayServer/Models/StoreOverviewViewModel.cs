namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreOverviewViewModel
{
    public bool IsLightningEnabled { get; set; }
    public bool IsDestinationSweepEnabled { get; set; }
    public ArkBalancesViewModel Balances { get; set; } = new();
    public string? WalletId { get; set; }
    public string? Destination { get; set; }
    public bool SignerAvailable { get; set; }
    public string? Wallet { get; set; }
    public string? DefaultAddress { get; set; }
    
    // Service connection status
    public string? ArkOperatorUrl { get; set; }
    public bool ArkOperatorConnected { get; set; }
    public string? ArkOperatorError { get; set; }
    
    public string? BoltzUrl { get; set; }
    public bool BoltzConnected { get; set; }
    public string? BoltzError { get; set; }
}