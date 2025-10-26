using BTCPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreSwapsViewModel : BasePagingViewModel
{
    public IReadOnlyCollection<ArkSwap> Swaps { get; set; } = [];
    public SearchString Search { get; set; } = new(null);
    public string? SearchText { get; set; }
    public string StoreId { get; set; }
    public bool Debug { get; set; }
    public HashSet<string> CachedSwapIds { get; set; } = new();

    public override int CurrentPageCount => Swaps.Count;
}
