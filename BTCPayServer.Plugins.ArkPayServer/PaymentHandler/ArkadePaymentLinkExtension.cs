using BTCPayServer.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentLinkExtension : IPaymentLinkExtension
{
    private readonly IServiceProvider _serviceProvider;

    public ArkadePaymentLinkExtension(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }
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
        
        // Add lightning invoice if available and within Boltz limits (prefer LN over LNURL)
        if (ShouldIncludeLightning(prompt).Result)
        {
            if (ln is not null)
            {
                builder.WithLightning(ln.Destination);
            }
            else if (lnurl is not null && _serviceProvider.GetServices<IPaymentLinkExtension>()
                         .FirstOrDefault(p => p.PaymentMethodId == lnurl.PaymentMethodId) is {} lnurlLink)
            {
                if (lnurlLink.GetPaymentLink(lnurl, urlHelper) is { } link)
                {
                    builder.WithLightning(link.Replace("lightning:", String.Empty));
                }
            }
        }
        
        return builder.Build();
    }

    private async Task<bool> ShouldIncludeLightning(PaymentPrompt prompt)
    {

        //TODO: cache storeids that use type-arkade LN connection strings and otherwise return true if not using arkade for ln
        
        // Get the invoice amount in satoshis
        var amountSats = (long)Money.Coins(prompt.Calculate().Due).Satoshi;

        // Allow top-up invoices (amount = 0)
        if (amountSats == 0)
        {
            return true;
        }

        // Get Boltz limits
        var boltzService = _serviceProvider.GetService<BoltzService>();
        if (boltzService == null)
        {
            // No Boltz service, include Lightning
            return true;
        }

        var boltzLimits = await boltzService.GetLimitsAsync(CancellationToken.None);
        if (boltzLimits == null)
        {
            return true;
        }

        // Include Lightning only if within Boltz limits
        return  amountSats >= boltzLimits.ReverseMinAmount && amountSats <= boltzLimits.ReverseMaxAmount;
    }
}
