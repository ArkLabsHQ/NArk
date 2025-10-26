using BTCPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreContractsViewModel: BasePagingViewModel
{
    public IReadOnlyCollection<ArkWalletContract> Contracts { get; set; } = [];
    public Dictionary<string, VTXO[]> ContractVtxos { get; set; } = new();
    public Dictionary<string, ArkSwap[]> ContractSwaps { get; set; } = new();
    public SearchString Search { get; set; }
    public string? SearchText { get; set; }
    public string StoreId { get; set; }
    public bool CanManageContracts { get; set; }
    public bool Debug { get; set; }
    public HashSet<string> CachedSwapScripts { get; set; } = new();

    public override int CurrentPageCount => Contracts.Count;
}