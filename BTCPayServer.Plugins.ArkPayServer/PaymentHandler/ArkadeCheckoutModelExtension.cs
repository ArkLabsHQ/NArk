using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadeCheckoutModelExtension: ICheckoutModelExtension
{
    private readonly IEnumerable<IPaymentLinkExtension> _paymentLinkExtensions;
    private readonly IPaymentLinkExtension _arkadePaymentLinkExtension;

    public ArkadeCheckoutModelExtension(
        IEnumerable<IPaymentLinkExtension> paymentLinkExtensions)
    {
        _paymentLinkExtensions = paymentLinkExtensions;
        _arkadePaymentLinkExtension = paymentLinkExtensions.Single(p => p.PaymentMethodId == ArkadePlugin.ArkadePaymentMethodId);
    }
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadePaymentMethodId;

    public string Image => "arkade.svg";

    public string Badge => "";//"👾";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: ArkadePaymentMethodHandler handler })
            return;
        
        context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;
        context.Model.ShowRecommendedFee = false;
        context.Model.InvoiceBitcoinUrlQR = _arkadePaymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper).ToUpperInvariant()
            .Replace("BITCOIN:","bitcoin:")
            .Replace("LIGHTNING=","lightning=")
            .Replace("ARK=","ark=");
        context.Model.InvoiceBitcoinUrl = _arkadePaymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper);
        if (context.Store.GetStoreBlob().OnChainWithLnInvoiceFallback)
        {
            var ln = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var lnurl = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
            var onchain = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
            var pmis = new List<PaymentMethodId> { ln, lnurl, onchain };
            context.Model.AvailablePaymentMethods.Where(method => pmis.Contains(method.PaymentMethodId)).ToList().ForEach(method => method.Displayed = false);
        }
        //
        // context.Model.InvoiceBitcoinUrl = _paymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper);
        // context.Model.InvoiceBitcoinUrlQR = context.Model.InvoiceBitcoinUrl;
        // context.Model.ShowPayInWalletButton = false;
        // context.Model.PaymentMethodCurrency = configurationItem.CurrencyDisplayName;

    }
}