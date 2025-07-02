using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

class ArkadeListenedContract
{
    public ArkadePromptDetails Details { get; set; }
    public string InvoiceId { get; set; }
}