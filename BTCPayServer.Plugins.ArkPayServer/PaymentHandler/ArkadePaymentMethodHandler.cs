using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.Routing;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentMethodHandler(
    BTCPayNetworkProvider networkProvider,
    ArkWalletService arkWalletService,
    LinkGenerator linkGenerator)
    : IPaymentMethodHandler, IHasNetwork
{
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadePaymentMethodId;

    public BTCPayNetwork Network { get; } = networkProvider.GetNetwork<BTCPayNetwork>("BTC");

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var store = context.Store;

        if (ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[PaymentMethodId]) is not ArkadePaymentMethodConfig
            arkadePaymentMethodConfig)
        {
            throw new PaymentMethodUnavailableException($"Arkade payment method not configured");
        }

        var details = new ArkadePaymentMethodDetails(arkadePaymentMethodConfig.WalletId);

        var contract = await arkWalletService.DerivePaymentContract(arkadePaymentMethodConfig.WalletId, CancellationToken.None);
        var address = contract.GetArkAddress();
        context.Prompt.Destination = address.ToString(Network.NBitcoinNetwork.ChainName == ChainName.Mainnet);;
        context.Prompt.PaymentMethodFee = 0m;

        context.TrackedDestinations.Add(context.Prompt.Destination);
        context.Prompt.Details = JObject.FromObject(details, Serializer);

    }


    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BTC";
        context.Prompt.Divisibility = 8;
        return Task.CompletedTask;
    }

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public ArkadePaymentMethodDetails ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<ArkadePaymentMethodDetails>(Serializer);
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details);
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<ArkadePaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadePaymentMethodHandler)}");
    }

    public ArkadePaymentData ParsePaymentDetails(JToken details)
    {
        return details.ToObject<ArkadePaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(ArkadePaymentData)}");
    }
    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }

    public void StripDetailsForNonOwner(object details)
    {
    }
}