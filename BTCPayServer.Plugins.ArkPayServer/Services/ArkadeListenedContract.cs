using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

internal record ArkadeListenedContract(ArkadePromptDetails Details, string InvoiceId);