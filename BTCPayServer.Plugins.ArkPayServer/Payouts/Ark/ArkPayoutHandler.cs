using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.Cache;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NArk;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Payment;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PayoutData = BTCPayServer.Data.PayoutData;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

public class ArkPayoutHandler(
    IOperatorTermsService operatorTermsService,
    EventAggregator eventAggregator,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    ApplicationDbContextFactory dbContextFactory,
    BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
    BTCPayNetworkProvider networkProvider,
    TrackedContractsCache trackedContractsCache
) : IPayoutHandler, IHasNetwork
{
    public string Currency => "BTC";
    public PayoutMethodId PayoutMethodId => ArkadePlugin.ArkadePayoutMethodId;

    public bool IsSupported(StoreData storeData)
    {
        var config =
            storeData
                .GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                    ArkadePlugin.ArkadePaymentMethodId,
                    paymentMethodHandlerDictionary,
                    true
                );

        return !string.IsNullOrWhiteSpace(config?.WalletId) && config.GeneratedByStore;
    }

    public Task TrackClaim(ClaimRequest claimRequest, PayoutData payoutData)
    {
        trackedContractsCache.TriggerUpdate();
        return Task.CompletedTask;
    }

    public async Task<(IClaimDestination destination, string error)> ParseClaimDestination(string destination,
        CancellationToken cancellationToken)
    {
        destination = destination.Trim();
        try
        {
            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);

            if (destination.StartsWith("bitcoin:", StringComparison.InvariantCultureIgnoreCase))
            {
                return (new ArkUriClaimDestination(new BitcoinUrlBuilder(destination, terms.Network)), null!);
            }

            return (
                new ArkAddressClaimDestination(ArkAddress.Parse(destination),
                    terms.Network.ChainName == ChainName.Mainnet), null!);
        }
        catch
        {
            return (null!, "A valid address was not provided");
        }
    }

    public (bool valid, string? error) ValidateClaimDestination(IClaimDestination claimDestination,
        PullPaymentBlob? pullPaymentBlob)
    {
        return (true, null);
    }

    public IPayoutProof ParseProof(PayoutData? payout)
    {
        if (payout?.Proof is null)
            return null!;
        var payoutMethodId = payout.GetPayoutMethodId();
        if (payoutMethodId is null)
            return null!;

        var parseResult = ParseProofType(payout.Proof);
        if (parseResult is null)
            return null!;
        
        if (parseResult.Value.MaybeType == ArkPayoutProof.Type)
        {
            return parseResult.Value.Object.ToObject<ArkPayoutProof>(
                JsonSerializer.Create(jsonSerializerSettings.GetSerializer(payoutMethodId))
            )!;
        }

        return parseResult.Value.Object.ToObject<ManualPayoutProof>()!;
    }

    private static (JObject Object, string? MaybeType)? ParseProofType(string? proof)
    {
        if (proof is null)
        {
            return null;
        }

        var obj = JObject.Parse(proof);
        var type = TryParseProofType(obj);

        
        return (obj, type);
    }

    private static string? TryParseProofType(JObject? proof)
    {
        if (proof is null) return null;

        if (!proof.TryGetValue("proofType", StringComparison.InvariantCultureIgnoreCase, out var proofType))
            return null;
        
        return proofType.Value<string>();
    }

    public void StartBackgroundCheck(Action<Type[]> subscribe)
    {
        subscribe([typeof(VTXOsUpdated)]);
    }

    public async Task BackgroundCheck(object o)
    {
        if (o is VTXOsUpdated vtxoEvent)
        {
            foreach (var vtxo in vtxoEvent.Vtxos)
            {
                await ApplyVtxo(vtxo);
            }
        }
    }

    private async Task ApplyVtxo(VTXO vtxo)
    {
        var terms = await operatorTermsService.GetOperatorTerms();
        var address = ArkAddress.FromScriptPubKey(new Script(vtxo.Script), terms.SignerKey)
            .ToString(terms.Network.ChainName == ChainName.Mainnet);
        
        await using var ctx = dbContextFactory.CreateContext();
        var payout = await ctx.Payouts
            .FirstOrDefaultAsync(data => data.DedupId == address && PayoutMethodId.ToString() == data.PayoutMethodId);

        if (payout is not null)
        {
            payout.State = PayoutState.Completed;
            SetProofBlob(payout, new ArkPayoutProof { TransactionId = uint256.Parse(vtxo.TransactionId) });
            await ctx.SaveChangesAsync();
            eventAggregator.Publish(new PayoutEvent(PayoutEvent.PayoutEventType.Updated, payout));
        }
        
    }

    public async Task<decimal> GetMinimumPayoutAmount(IClaimDestination claimDestination)
    {
        var terms = await operatorTermsService.GetOperatorTerms();
        return terms.Dust.ToDecimal(MoneyUnit.BTC);
    }

    public Dictionary<PayoutState, List<(string Action, string Text)>> GetPayoutSpecificActions()
    {
        return new Dictionary<PayoutState, List<(string Action, string Text)>>
        {
             {PayoutState.AwaitingPayment, [("reject-payment", "Reject payout transaction")]}
         };
    }

    public Task<StatusMessageModel> DoSpecificAction(string action, string[] payoutIds, string storeId)
    {
        return Task.FromResult<StatusMessageModel>(null!);
    }

    public async Task<IActionResult> InitiatePayment(string[] payoutIds)
    {
        var terms = await operatorTermsService.GetOperatorTerms();

        await using var ctx = dbContextFactory.CreateContext();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var payouts = await ctx.Payouts
            .Include(data => data.PullPaymentData)
            .Where(data => payoutIds.Contains(data.Id)
                           && PayoutMethodId.ToString() == data.PayoutMethodId
                           && data.State == PayoutState.AwaitingPayment)
            .ToListAsync();

        var storeId = payouts.First().StoreDataId;

        List<string> bip21s = [];

        foreach (var payout in payouts)
        {
            if (payout.Proof != null)
            {
                continue;
            }

            var blob = payout.GetBlob(jsonSerializerSettings);
            if (payout.GetPayoutMethodId() != PayoutMethodId)
                continue;
            var claim = await ParseClaimDestination(blob.Destination, CancellationToken.None);
            switch (claim.destination)
            {
                case ArkUriClaimDestination uriClaimDestination:
                    uriClaimDestination.BitcoinUrl.Amount = new Money(payout.Amount.Value, MoneyUnit.BTC);
                    var newUri = new UriBuilder(uriClaimDestination.BitcoinUrl.Uri);
                    BTCPayServerClient.AppendPayloadToQuery(newUri,
                        new KeyValuePair<string, object>("payout", payout.Id));
                    bip21s.Add(newUri.Uri.ToString());
                    break;
                case ArkAddressClaimDestination addressClaimDestination:
                    var builder = new PaymentUrlBuilder("bitcoin")
                    {
                        Host = addressClaimDestination.Address.ToString(terms.Network.ChainName == ChainName.Mainnet)
                    };
                    builder.QueryParams.Add("amount", payout.Amount.Value.ToString());
                    bip21s.Add(builder.ToString());
                    break;
            }
        }

        return new RedirectToActionResult("SpendOverview", "Ark", new { storeId = storeId, destinations = bip21s });
    }

    public BTCPayNetwork Network => networkProvider.GetNetwork<BTCPayNetwork>(Currency);
    
    private void SetProofBlob(PayoutData data, ArkPayoutProof blob)
    {
         data.SetProofBlob(blob, jsonSerializerSettings.GetSerializer(data.GetPayoutMethodId()));
    }
}