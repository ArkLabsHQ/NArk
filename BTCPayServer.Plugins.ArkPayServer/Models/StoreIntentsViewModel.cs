using BTCPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreIntentsViewModel : BasePagingViewModel
{
    public IReadOnlyCollection<ArkIntent> Intents { get; set; } = [];
    public Dictionary<int, ArkIntentVtxo[]> IntentVtxos { get; set; } = new();
    public SearchString Search { get; set; } = new(null);
    public string? SearchText { get; set; }
    public string StoreId { get; set; }

    public override int CurrentPageCount => Intents.Count;
}
