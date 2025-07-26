// using BTCPayServer;
// using BTCPayServer.Abstractions.Models;
// using BTCPayServer.Client;
// using BTCPayServer.Client.Models;
// using BTCPayServer.Data;
// using BTCPayServer.Events;
// using BTCPayServer.HostedServices;
// using BTCPayServer.Logging;
// using BTCPayServer.Payments;
// using BTCPayServer.Payments.Bitcoin;
// using BTCPayServer.Payouts;
// using BTCPayServer.Plugins.ArkPayServer;
// using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
// using BTCPayServer.Plugins.ArkPayServer.Services;
// using BTCPayServer.Services;
// using BTCPayServer.Services.Invoices;
// using BTCPayServer.Services.Notifications;
// using BTCPayServer.Services.Notifications.Blobs;
// using Microsoft.AspNetCore.Mvc;
// using Microsoft.EntityFrameworkCore;
// using Microsoft.Extensions.Logging;
// using NArk;
// using NArk.Services;
// using NArk.Services.Models;
// using NBitcoin;
// using NBitcoin.Payment;
// using NBXplorer.Models;
// using Newtonsoft.Json;
// using Newtonsoft.Json.Linq;
// using NewBlockEvent = BTCPayServer.Events.NewBlockEvent;
// using PayoutData = BTCPayServer.Data.PayoutData;
// using StoreData = BTCPayServer.Data.StoreData;
//
// public class ArkPayoutHandler : IPayoutHandler
// {
//     public string Currency { get; }
//     private readonly ArkSubscriptionService _arkSubscriptionService;
//     private readonly IOperatorTermsService _operatorTermsService;
//     private readonly PaymentMethodHandlerDictionary _paymentHandlers;
//     private readonly ExplorerClientProvider _explorerClientProvider;
//     private readonly BTCPayNetworkJsonSerializerSettings _jsonSerializerSettings;
//     private readonly ApplicationDbContextFactory _dbContextFactory;
//     private readonly NotificationSender _notificationSender;
//     private readonly EventAggregator _eventAggregator;
//     private readonly TransactionLinkProviders _transactionLinkProviders;
//     private readonly ILogger<ArkPayoutHandler> _logger;
//
//     PayoutMethodId IHandler<PayoutMethodId>.Id => PayoutMethodId;
//     public PayoutMethodId PayoutMethodId  => ArkadePlugin.ArkadePayoutMethodId;
//     public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadePaymentMethodId;
//     public WalletRepository WalletRepository { get; }
//
//     public ArkPayoutHandler(
//         
//         ArkSubscriptionService arkSubscriptionService,
//         IOperatorTermsService operatorTermsService,
//         PaymentMethodHandlerDictionary handlers,
//         WalletRepository walletRepository,
//         ExplorerClientProvider explorerClientProvider,
//         BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
//         ApplicationDbContextFactory dbContextFactory,
//         NotificationSender notificationSender,
//         EventAggregator eventAggregator,
//         TransactionLinkProviders transactionLinkProviders,
//         ILogger<ArkPayoutHandler> logger)
//     {
//         _arkSubscriptionService = arkSubscriptionService;
//         _operatorTermsService = operatorTermsService;
//         _paymentHandlers = handlers;
//         WalletRepository = walletRepository;
//         _explorerClientProvider = explorerClientProvider;
//         _jsonSerializerSettings = jsonSerializerSettings;
//         _dbContextFactory = dbContextFactory;
//         _notificationSender = notificationSender;
//         Currency = "BTC";
//         _eventAggregator = eventAggregator;
//         _transactionLinkProviders = transactionLinkProviders;
//         _logger = logger;
//     }
//
//     public async Task TrackClaim(ClaimRequest claimRequest, PayoutData payoutData)
//     {
//         if (claimRequest.Destination is IArkClaimDestination arkClaimDestination)
//         {
//             await _arkSubscriptionService.UpdateManualSubscriptionAsync(arkClaimDestination.Address.ScriptPubKey.ToHex(), true, CancellationToken.None);
//         }
//     }
//
//     public async Task<(IClaimDestination destination, string error)> ParseClaimDestination(string destination, CancellationToken cancellationToken)
//     {
//         
//         destination = destination.Trim();
//         try
//         {
//             var terms = await _operatorTermsService.GetOperatorTerms(cancellationToken);
//             
//             if (destination.StartsWith($"bitcoin:", StringComparison.OrdinalIgnoreCase))
//             {
//                 return (new ArkUriClaimDestination(new BitcoinUrlBuilder(destination, terms.Network)), null);
//             }
//
//             return (new ArkAddressClaimDestination(ArkAddress.Parse(destination),terms.Network.ChainName == ChainName.Mainnet), null);
//         }
//         catch
//         {
//             return (null, "A valid address was not provided");
//         }
//     }
//
//     public (bool valid, string error) ValidateClaimDestination(IClaimDestination claimDestination, PullPaymentBlob pullPaymentBlob)
//     {
//         return (true, null);
//     }
//
//     public IPayoutProof ParseProof(PayoutData payout)
//     {
//         if (payout?.Proof is null)
//             return null;
//         var payoutMethodId = payout.GetPayoutMethodId();
//         if (payoutMethodId is null)
//             return null;
//         ParseProofType(payout.Proof, out var raw, out var proofType);
//         if (proofType ==ArkPayoutProof.Type)
//         {
//
//             var res = raw.ToObject<ArkPayoutProof>(
//                 JsonSerializer.Create(_jsonSerializerSettings.GetSerializer(payoutMethodId)));
//             
//             return res;
//         }
//         return raw.ToObject<ManualPayoutProof>();
//     }
//
//     public static void ParseProofType(string proof, out JObject obj, out string type)
//     {
//         type = null;
//         if (proof is null)
//         {
//             obj = null;
//             return;
//         }
//
//         obj = JObject.Parse(proof);
//         TryParseProofType(obj, out type);
//     }
//
//     public static bool TryParseProofType(JObject proof, out string type)
//     {
//         type = null;
//         if (proof is null)
//         {
//             return false;
//         }
//
//         if (!proof.TryGetValue("proofType", StringComparison.InvariantCultureIgnoreCase, out var proofType))
//             return false;
//         type = proofType.Value<string>();
//         return true;
//     }
//
//     public void StartBackgroundCheck(Action<Type[]> subscribe)
//     {
//         subscribe([typeof(VTXOsUpdated)]);
//     }
//
//     public async Task BackgroundCheck(object o)
//     {
//         if (o is VTXOsUpdated vtXOsUpdated)
//         {
//             await UpdatePayoutsAwaitingForPayment(vtXOsUpdated);
//         }
//         
//     }
//
//     public async Task<decimal> GetMinimumPayoutAmount(IClaimDestination claimDestination)
//     {
//         var terms = await _operatorTermsService.GetOperatorTerms();
//         return terms.Dust.ToDecimal(MoneyUnit.BTC);
//     }
//
//
//     public Dictionary<PayoutState, List<(string Action, string Text)>> GetPayoutSpecificActions()
//     {
//         return new Dictionary<PayoutState, List<(string Action, string Text)>>()
//         {
//             {PayoutState.AwaitingPayment, new List<(string Action, string Text)>()
//             {
//                 ("reject-payment", "Reject payout transaction")
//             }}
//         };
//     }
//
//     public async Task<StatusMessageModel> DoSpecificAction(string action, string[] payoutIds, string storeId)
//     {
//       
//
//         return null;
//     }
//
//     public bool IsSupported(StoreData storeData)
//     {
//         return !string.IsNullOrEmpty(storeData.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(PaymentMethodId, _paymentHandlers, true)?.WalletId);
//           }
//
//     public async Task<IActionResult> InitiatePayment(string[] payoutIds)
//     {
//         await using var ctx = this._dbContextFactory.CreateContext();
//         ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
//         var payouts = await ctx.Payouts.Include(data => data.PullPaymentData)
//             .Where(data => payoutIds.Contains(data.Id)
//                            && PayoutMethodId.ToString() == data.PayoutMethodId
//                            && data.State == PayoutState.AwaitingPayment)
//             .ToListAsync();
//
//         var pullPaymentIds = payouts.Select(data => data.PullPaymentDataId).Distinct().Where(s => s != null).ToArray();
//         var storeId = payouts.First().StoreDataId;
//         List<string> bip21 = new List<string>();
//         foreach (var payout in payouts)
//         {
//             if (payout.Proof != null)
//             {
//                 continue;
//             }
//             var blob = payout.GetBlob(_jsonSerializerSettings);
//             if (payout.GetPayoutMethodId() != PayoutMethodId)
//                 continue;
//             var claim = await ParseClaimDestination(blob.Destination, default);
//             switch (claim.destination)
//             {
//                 case UriClaimDestination uriClaimDestination:
//                     uriClaimDestination.BitcoinUrl.Amount = new Money(payout.Amount.Value, MoneyUnit.BTC);
//                     var newUri = new UriBuilder(uriClaimDestination.BitcoinUrl.Uri);
//                     BTCPayServerClient.AppendPayloadToQuery(newUri, new KeyValuePair<string, object>("payout", payout.Id));
//                     bip21.Add(newUri.Uri.ToString());
//                     break;
//                 case AddressClaimDestination addressClaimDestination:
//                     var bip21New = Network.GenerateBIP21(addressClaimDestination.Address.ToString(), payout.Amount.Value);
//                     bip21New.QueryParams.Add("payout", payout.Id);
//                     bip21.Add(bip21New.ToString());
//                     break;
//             }
//         }
//         if (bip21.Any())
//             return new RedirectToActionResult("WalletSend", "UIWallets", new { walletId = new WalletId(storeId, Network.CryptoCode).ToString(), bip21 });
//         return new RedirectToActionResult("Payouts", "UIWallets", new
//         {
//             walletId = new WalletId(storeId, Network.CryptoCode).ToString(),
//             pullPaymentId = pullPaymentIds.Length == 1 ? pullPaymentIds.First() : null
//         });
//     }
//
//     private async Task UpdatePayoutsAwaitingForPayment(VTXOsUpdated vtXOsUpdated)
//     {
//         try
//         {
//             var destinations = vtXOsUpdated.Vtxos.Select(vtxo => vtxo.Script).ToHashSet();
//
//             await using var ctx = _dbContextFactory.CreateContext();
//             var payouts = await ctx.Payouts
//                 .Include(o => o.StoreData)
//                 .Include(o => o.PullPaymentData)
//                 .Where(p => p.State == PayoutState.AwaitingPayment)
//                 .Where(p => p.PayoutMethodId == PaymentMethodId.ToString())
// #pragma warning disable CA1307 // Specify StringComparison
//                 .Where(p => destinations.Contains(p.DedupId))
// #pragma warning restore CA1307 // Specify StringComparison
//                 .ToListAsync();
//
//             if (payouts.Any() is not true)
//                 return;
//             if (payout.Amount is null ||
//                 // The round up here is not strictly necessary, this is temporary to fix existing payout before we
//                 // were properly roundup the crypto amount
//                 destinationSum !=
//                 BTCPayServer.Extensions.RoundUp(payout.Amount.Value, Network.Divisibility))
//                 return;
//
//             var derivationSchemeSettings = payout.StoreData
//                 .GetDerivationSchemeSettings(_paymentHandlers, Network.CryptoCode)?.AccountDerivation;
//             if (derivationSchemeSettings is null)
//                 return;
//
//             var storeWalletMatched = (await _explorerClientProvider.GetExplorerClient(Network.CryptoCode)
//                 .GetTransactionAsync(derivationSchemeSettings,
//                     newTransaction.NewTransactionEvent.TransactionData.TransactionHash));
//             //if the wallet related to the store of the payout does not have the tx: it has been paid externally
//             var isInternal = storeWalletMatched is not null;
//
//             var proof = ParseProof(payout) as PayoutTransactionOnChainBlob ??
//                         new PayoutTransactionOnChainBlob() { Accounted = isInternal };
//             var txId = newTransaction.NewTransactionEvent.TransactionData.TransactionHash;
//             if (!proof.Candidates.Add(txId))
//                 return;
//             if (isInternal)
//             {
//                 payout.State = PayoutState.InProgress;
//                 await WalletRepository.AddWalletTransactionAttachment(
//                     new WalletId(payout.StoreDataId, Network.CryptoCode),
//                     newTransaction.NewTransactionEvent.TransactionData.TransactionHash,
//                     Attachment.Payout(payout.PullPaymentDataId, payout.Id));
//             }
//             else
//             {
//                 await _notificationSender.SendNotification(new StoreScope(payout.StoreDataId),
//                     new ExternalPayoutTransactionNotification()
//                     {
//                         PaymentMethod = payout.PayoutMethodId,
//                         PayoutId = payout.Id,
//                         StoreId = payout.StoreDataId
//                     });
//             }
//
//             proof.TransactionId ??= txId;
//             SetProofBlob(payout, proof);
//             await ctx.SaveChangesAsync();
//             _eventAggregator.Publish(new PayoutEvent(PayoutEvent.PayoutEventType.Updated,payout));
//         }
//         catch (Exception ex)
//         {
//             _logger.LogWarning(ex, "Error while processing a transaction in the pull payment hosted service");
//         }
//     }
//
//     public void SetProofBlob(PayoutData data, ArkPayoutProof blob)
//     {
//         data.SetProofBlob(blob, _jsonSerializerSettings.GetSerializer(data.GetPayoutMethodId()));
//
//     }
// }
