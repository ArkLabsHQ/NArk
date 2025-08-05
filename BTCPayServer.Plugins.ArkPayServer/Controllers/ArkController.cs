using System.Globalization;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using NArk;
using NArk.Services;
using NArk.Services.Models;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Payment;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public class ArkStoreWalletViewModel
{
    public string? WalletId { get; set; }
    public string? Destination { get; set; }

    public bool SignerAvailable { get; set; }
    public Dictionary<ArkWalletContract, VTXO[]>? Contracts { get; set; }
    public bool LNEnabled { get; set; }

    public string? Wallet { get; set; }
}

[Route("plugins/ark")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ArkController : Controller
{
    private readonly StoreRepository _storeRepository;
    private readonly ArkWalletService _arkWalletService;
    private readonly ArkadeWalletSignerProvider _walletSignerProvider;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly IOperatorTermsService _operatorTermsService;
    private readonly IAuthorizationService _authorizationService;
    private readonly ArkadeSpender _arkadeSpender;

    public ArkController(
        StoreRepository storeRepository,
        ArkWalletService arkWalletService,
        ArkadeWalletSignerProvider walletSignerProvider,
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        IOperatorTermsService operatorTermsService,
        IAuthorizationService authorizationService,
        ArkadeSpender arkadeSpender)
    {
        _storeRepository = storeRepository;
        _arkWalletService = arkWalletService;
        _walletSignerProvider = walletSignerProvider;
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _operatorTermsService = operatorTermsService;
        _authorizationService = authorizationService;
        _arkadeSpender = arkadeSpender;
    }

    [HttpGet("stores/{storeId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SetupStore(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId == null)
        {
            return View(new ArkStoreWalletViewModel());
        }

        var includeData = config.GeneratedByStore ||
                          (await _authorizationService.AuthorizeAsync(User, null,
                              new PolicyRequirement(Policies.CanModifyServerSettings))).Succeeded;
        var walletInfo = await _arkWalletService.GetWalletInfo(config.WalletId, includeData);
        if (walletInfo is null)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "Wallet not found in records anymore. Please re-import the wallet.";
            return await SetupStore(storeId, new ArkStoreWalletViewModel());
        }

        var lnPmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig =
            store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lnPmi, _paymentMethodHandlerDictionary);

        return View(new ArkStoreWalletViewModel()
        {
            WalletId = config.WalletId,
            Destination = walletInfo.Value.Destination,
            SignerAvailable =
                await _walletSignerProvider.GetSigner(config.WalletId, HttpContext.RequestAborted) is not null,
            Contracts = walletInfo.Value.Contracts ,
            LNEnabled =
                lnConfig?.ConnectionString?.StartsWith("type=arkade") is true && store.IsLightningEnabled("BTC"),
            Wallet =  walletInfo.Value.Wallet
        });
    }

    [HttpPost("stores/{storeId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SetupStore(string storeId, ArkStoreWalletViewModel model, string? command = null)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var terms = await _operatorTermsService.GetOperatorTerms();


        var lnPmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig =
            store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lnPmi, _paymentMethodHandlerDictionary);

        var lnEnabled =
            lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;
        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        var nsecKnownToStore = config?.GeneratedByStore ?? false;
        var generate = false;

        if (command == "configure" && config?.WalletId == null)
        {
            switch (model.Wallet)
            {
                case { } wallet when wallet.StartsWith("nsec", StringComparison.InvariantCultureIgnoreCase):
                    nsecKnownToStore = true;
                    break;
                case { } wallet when ArkAddress.TryParse(wallet, out var addr):
                    if (!terms.SignerKey.ToBytes().SequenceEqual(addr!.ServerKey.ToBytes()))
                    {
                        ModelState.AddModelError(nameof(model.Wallet), "Invalid destination address.");
                        return View(model);
                    }

                    model.Destination = wallet;
                    nsecKnownToStore = true;
                    generate = true;
                    break;
                case null:
                case "":
                    generate = true;
                    break;
                case { } potentialWalletId when HexEncoder.IsWellFormed(potentialWalletId) &&
                                                Encoders.Hex.DecodeData(potentialWalletId) is
                                                    {Length: 32} potentialwalletBytes &&
                                                ECXOnlyPubKey.TryCreate(potentialwalletBytes, out _):
                    generate = false;
                    nsecKnownToStore = false;
                    model.WalletId = potentialWalletId;
                    model.Wallet = null;
                    if (!await _arkWalletService.WalletExists(potentialWalletId, HttpContext.RequestAborted))
                    {
                        ModelState.AddModelError(nameof(model.Wallet), "Unsupported value.");
                    }

                    break;
                default:
                    ModelState.AddModelError(nameof(model.Wallet), "Unsupported value.");
                    return View(model);
            }
        }

        if (generate)
        {
            nsecKnownToStore = true;
            var key = RandomUtils.GetBytes(32)!;
            var encoder = Encoders.Bech32("nsec");
            encoder.SquashBytes = true;
            encoder.StrictLength = false;
            var nsec = encoder.EncodeData(key, Bech32EncodingType.BECH32);
            model.Wallet = nsec;
        }


        if (command == "enable-ln" && config?.WalletId != null)
        {
            lnConfig = new LightningPaymentMethodConfig()
            {
                ConnectionString = $"type=arkade;wallet-id={config.WalletId}",
            };
            store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[lnPmi], lnConfig);
            var blob = store.GetStoreBlob();
            blob.SetExcluded(lnPmi, false);
            store.SetStoreBlob(blob);
            await _storeRepository.UpdateStore(store);
            TempData[WellKnownTempData.SuccessMessage] = "Lightning enabled";
            return RedirectToAction(nameof(SetupStore), new {storeId});
        }

        if (command == "poll-scripts" && config?.WalletId != null)
        {
            await _arkWalletService.UpdateBalances(config.WalletId, true);
            TempData[WellKnownTempData.SuccessMessage] = "Scripts polled";
            return RedirectToAction(nameof(SetupStore), new {storeId});
        }

        if (command == "clear" && config?.WalletId != null)
        {
            store.SetPaymentMethodConfig(ArkadePlugin.ArkadePaymentMethodId, null);
            if (lnEnabled)
            {
                store.SetPaymentMethodConfig(lnPmi, null);
            }

            await _storeRepository.UpdateStore(store);
            TempData[WellKnownTempData.SuccessMessage] = $"Ark Payment method cleared.";
            return RedirectToAction("SetupStore", new {storeId});
        }
        
        if(HttpContext.Request.Form.TryGetValue("payment", out var payment) && !string.IsNullOrEmpty(payment))
        {
            return await Spend(payment!);
        }

        if (!string.IsNullOrEmpty(model.Destination))
        {
            if (!ArkAddress.TryParse(model.Destination, out var addr))
            {
                ModelState.AddModelError(nameof(model.Destination), "Invalid destination address.");
                return View(model);
            }

            if (!terms.SignerKey.ToBytes().SequenceEqual(addr!.ServerKey.ToBytes()))
            {
                ModelState.AddModelError(nameof(model.Wallet), "Invalid destination address.");
                return View(model);
            }
        }

        if (model.Wallet is not null)
        {
            try
            {
                model.WalletId = await _arkWalletService.Upsert(model.Wallet, model.Destination, nsecKnownToStore,
                    HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Could not update wallet: " + ex.Message;
                return View(model);
            }
        }

        config = new ArkadePaymentMethodConfig(model.WalletId, nsecKnownToStore);
        store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], config);
        if (lnEnabled || lnConfig is null)
        {
            lnConfig = new LightningPaymentMethodConfig()
            {
                ConnectionString = $"type=arkade;wallet-id={model.WalletId}",
            };
            store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[lnPmi], lnConfig);
            var blob = store.GetStoreBlob();
            blob.SetExcluded(lnPmi, false);
            store.SetStoreBlob(blob);
        }

        await _storeRepository.UpdateStore(store);

        TempData[WellKnownTempData.SuccessMessage] = $"Ark Payment method updated.";


        return RedirectToAction("SetupStore", new {storeId});
    }

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, _paymentMethodHandlerDictionary);
    }

    private async Task<IActionResult> Spend(string destination)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();
        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId == null)
            return NotFound();
        var signer = await _walletSignerProvider.GetSigner(config.WalletId, HttpContext.RequestAborted);
        if (signer is null)
            return NotFound();
        ArkOperatorTerms terms;
        try
        {
            terms = await _operatorTermsService.GetOperatorTerms(HttpContext.RequestAborted);
        }
        catch (Exception e)
        {
            return NotFound();
        }


        if (destination.Replace("lightning:", "", StringComparison.InvariantCultureIgnoreCase) is { } lnbolt11 &&
            BOLT11PaymentRequest.TryParse(lnbolt11, out var bolt11, terms.Network))
        {
            var lnConfig =
                store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(PaymentTypes.LN.GetPaymentMethodId("BTC"),
                    _paymentMethodHandlerDictionary);
            var lnClient = _paymentMethodHandlerDictionary.GetLightningHandler("BTC").CreateLightningClient(lnConfig);
            var resp = await lnClient.Pay(bolt11.ToString(), HttpContext.RequestAborted);
            if (resp.Result == PayResult.Ok)
            {
                TempData[WellKnownTempData.SuccessMessage] = $"Payment sent to {bolt11}";
                return RedirectToAction(nameof(SetupStore), new {storeId = store.Id});
            }

            TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: {resp.Details.Status}";
            return RedirectToAction(nameof(SetupStore), new {storeId = store.Id});
        }
        else if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
                 uri.Scheme.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase))
        {
            var host = uri.AbsoluteUri.Substring(uri.Scheme.Length + 1).Split('?')[0]; // uri.Host is empty so we must parse it ourselves

            var qs = uri.ParseQueryString();
            if ( ArkAddress.TryParse(host, out var address) ||
                (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out address)))
            {
                var amount = decimal.Parse(qs["amount"] ?? "0", CultureInfo.InvariantCulture);

                try
                {
                    var txId = await _arkadeSpender.Spend(config.WalletId, [new TxOut(Money.Coins(amount), address)],
                        HttpContext.RequestAborted);
                    
                    await _arkWalletService.UpdateBalances(config.WalletId, true);
                    TempData[WellKnownTempData.SuccessMessage] = $"Payment sent to {address.ToString(terms.Network.ChainName == ChainName.Mainnet)} with txid {txId}";
                }
                catch (Exception e)
                {
                    TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: {e.Message}";
                }
            }
        }

        return RedirectToAction(nameof(SetupStore), new {storeId = store.Id});
    }
}