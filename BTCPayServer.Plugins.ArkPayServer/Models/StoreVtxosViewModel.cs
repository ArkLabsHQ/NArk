using BTCPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreVtxosViewModel : BasePagingViewModel
{
    public IReadOnlyCollection<VTXO> Vtxos { get; set; } = [];
    public SearchString Search { get; set; }
    public string? SearchText { get; set; }
    public string? SearchTerm { get; set; }
    public string StoreId { get; set; }

    public override int CurrentPageCount => Vtxos.Count;
}
