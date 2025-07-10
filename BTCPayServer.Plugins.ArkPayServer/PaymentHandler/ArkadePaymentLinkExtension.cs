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
        var due = prompt.Calculate().Due;
        return $"bitcoin:?ark={prompt.Destination}?amount={due.ToString(CultureInfo.InvariantCulture)}";
    }
}
