using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


namespace BTCPayServer.Plugins.Cashu.PaymentHandlers;
public class ArkadePaymentMethodHandler(
    BTCPayNetworkProvider networkProvider,
ArkService arkService,
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

        var details = new ArkadePaymentMethodDetails()
        {
            Wallet = arkadePaymentMethodConfig.WalletId
        };

        var contract = await arkService.DerivePaymentContract(arkadePaymentMethodConfig.WalletId, CancellationToken.None);
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