namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class ArkBalancesViewModel
{
    public decimal AvailableBalance { get; set; }
    public decimal RecoverableBalance { get; set; }
    public decimal LockedBalance { get; set; }
}
