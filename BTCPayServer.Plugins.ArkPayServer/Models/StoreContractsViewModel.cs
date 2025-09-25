using BTCPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreContractsViewModel: BasePagingViewModel
{
    public IReadOnlyCollection<ArkWalletContract> Contracts { get; set; } = [];

    public override int CurrentPageCount => Contracts.Count;
}