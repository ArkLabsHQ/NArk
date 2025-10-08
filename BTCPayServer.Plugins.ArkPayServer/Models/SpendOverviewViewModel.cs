namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class SpendOverviewViewModel
{
    public decimal AvailableBalance { get; set; }

    public List<string> PrefilledDestination { get; set; } = [];
    public string? Destination { get; set; }
}