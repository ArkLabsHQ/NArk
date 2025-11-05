using BTCPayServer.Abstractions.Services;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Payments.LNURLPay;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Plugin hook filter that validates Boltz limits for LNURL requests when Arkade Lightning is enabled
/// </summary>
public class ArkadeLNURLPayRequestFilter(
    BoltzService boltzService,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary
) : PluginHookFilter<StoreLNURLPayRequest>
{
    public override string Hook => "modify-lnurlp-request";

    public override async Task<StoreLNURLPayRequest> Execute(StoreLNURLPayRequest request)
    {
        if (request?.Tag != "payRequest" || request.Store == null)
            return request;

        // Check if Arkade Lightning is enabled for this store
        var lnPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig = request.Store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
            lnPaymentMethodId,
            paymentMethodHandlerDictionary);
        var isArkadeLightningEnabled =
            lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;

        if (!isArkadeLightningEnabled)
        {
            // Not using Arkade Lightning, don't modify limits
            return request;
        }
        
        // Get Boltz limits
        var boltzLimits = await boltzService.GetLimitsAsync(CancellationToken.None);
        if (boltzLimits == null)
        {
            // Boltz unavailable - disable LNURL since we can't fulfill Lightning payments
            return null;
        }

        // Apply Boltz limits to the LNURL request
        // MinSendable and MaxSendable are in millisatoshis
        var boltzMinMsat = boltzLimits.ReverseMinAmount * 1000L;
        var boltzMaxMsat = boltzLimits.ReverseMaxAmount * 1000L;

        // Constrain the LNURL limits to Boltz limits
        if (request.MinSendable is not null)
        {
            request.MinSendable = Math.Max(request.MinSendable, boltzMinMsat);
        }
        else
        {
            request.MinSendable = boltzMinMsat;
        }

        if (request.MaxSendable is not null)
        {
            request.MaxSendable = Math.Min(request.MaxSendable, boltzMaxMsat);
        }
        else
        {
            request.MaxSendable = boltzMaxMsat;
        }

        // If min > max after applying constraints, the request is invalid
        if (request.MinSendable > request.MaxSendable)
        {
            // Return null or throw to indicate LNURL should not be available
            return null;
        }

        return request;
    }
}
