using System.Globalization;
using BTCPayServer.Models;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentLinkExtension : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = ArkadePlugin.ArkadePaymentMethodId;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        var onchain = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.CHAIN.GetPaymentMethodId("BTC"));
        var ln = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LN.GetPaymentMethodId("BTC"));
        var lnurl = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LNURL.GetPaymentMethodId("BTC"));

        if (string.IsNullOrEmpty(onchain?.Destination) && ln is  null && lnurl is null)
        {
            return
                $"bitcoin:{prompt.Destination}?amount={prompt.Calculate().Due.ToString(CultureInfo.InvariantCulture)}";
            
        }

        var res = $"bitcoin:{onchain?.Destination??String.Empty}?amount={prompt.Calculate().Due.ToString(CultureInfo.InvariantCulture)}&ark={prompt.Destination}";
        
        if (ln is not null)
        {
            res += $"&lightning={ln.Destination}";
        }
        else if (lnurl is not null)
        {
            res += $"&lightning={lnurl.Destination}";
        }
        return res;
    }
}
