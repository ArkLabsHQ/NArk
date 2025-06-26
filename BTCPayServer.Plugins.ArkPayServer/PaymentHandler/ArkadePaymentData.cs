using System.Globalization;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Cashu.PaymentHandlers;

public class ArkadePaymentData
{
    public  string Outpoint { get; set; }
}

public class ArkadePaymentMethodConfig
{
    public string WalletId { get; set; }
}


public class ArkadePaymentPromptDetails
{
    public string Wallet { get; set; }
}
public class ArkadePaymentMethodDetails
{
    public string Wallet { get; set; }
}

public class ArkadePaymentLinkExtension : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; }
    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        var due = prompt.Calculate().Due;
        return $"bitcoin:{prompt.Destination}?amount={due.ToString(CultureInfo.InvariantCulture)}";
    }
}