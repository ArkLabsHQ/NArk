using BTCPayServer.Models;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentLinkExtension : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = ArkadePlugin.ArkadePaymentMethodId;

    public string GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        // Get other payment methods if available
        var onchain = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.CHAIN.GetPaymentMethodId("BTC"));
        var ln = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LN.GetPaymentMethodId("BTC"));
        var lnurl = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LNURL.GetPaymentMethodId("BTC"));

        var amount = prompt.Calculate().Due;
        
        // Build BIP21 URI using the helper
        var builder = ArkadeBip21Builder.Create()
            .WithArkAddress(prompt.Destination)
            .WithAmount(amount);
        
        // Add onchain address if available
        if (!string.IsNullOrEmpty(onchain?.Destination))
        {
            builder.WithOnchainAddress(onchain.Destination);
        }
        
        // Add lightning invoice if available (prefer LN over LNURL)
        if (ln is not null)
        {
            builder.WithLightning(ln.Destination);
        }
        else if (lnurl is not null)
        {
            builder.WithLightning(lnurl.Destination);
        }
        
        return builder.Build();
    }
}
