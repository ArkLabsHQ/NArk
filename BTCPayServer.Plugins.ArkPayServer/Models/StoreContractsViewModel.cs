using BTCPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreContractsViewModel: BasePagingViewModel
{
    public IReadOnlyCollection<ArkWalletContract> Contracts { get; set; } = [];
    public Dictionary<string, VTXO[]> ContractVtxos { get; set; } = new();
    public SearchString Search { get; set; }
    public string? SearchText { get; set; }
    public bool LoadVtxos { get; set; }
    public bool IncludeSpent { get; set; }
    public bool IncludeRecoverable { get; set; }
    public string StoreId { get; set; }

    public override int CurrentPageCount => Contracts.Count;
}