
using System.Globalization;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk;
using NArk.Models;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

[Route("plugins/ark")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ArkController(
    StoreRepository storeRepository,
    ArkWalletService arkWalletService,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    IOperatorTermsService operatorTermsService,
    ArkadeWalletSignerProvider walletSignerProvider,
    ArkadeSpender arkadeSpender) : Controller
{
    [HttpGet("stores/{storeId}/initial-setup")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult InitialSetup(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId == null)
        {
            return View(new InitialWalletSetupViewModel());
        }

        return RedirectToAction("StoreOverview", new { storeId });
    }

    [HttpPost("stores/{storeId}/initial-setup")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> InitialSetup(string storeId, InitialWalletSetupViewModel model)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        try
        {
            var walletSettings = await GetFromInputWallet(model.Wallet);

            if (walletSettings.Wallet is not null)
            {
                try
                {
                    walletSettings = walletSettings with
                    {
                        WalletId =
                            await arkWalletService.Upsert(
                                walletSettings.Wallet,
                                walletSettings.Destination,
                                walletSettings.IsOwnedByStore,
                                HttpContext.RequestAborted
                            )
                    };
                }
                catch (Exception ex)
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Could not update wallet: " + ex.Message;
                    return View(model);
                }
            }

            var config = new ArkadePaymentMethodConfig(walletSettings.WalletId!, walletSettings.IsOwnedByStore);
            store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], config);

            await storeRepository.UpdateStore(store);

            TempData[WellKnownTempData.SuccessMessage] = $"Ark Payment method updated.";

            return RedirectToAction("StoreOverview", new { storeId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(nameof(model.Wallet), ex.Message);
            return View(model);
        }
    }

    [HttpGet("stores/{storeId}/overview")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult StoreOverview()
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup");

        var destination = arkWalletService.GetWalletDestination(config.WalletId);

        return View(new StoreOverviewViewModel { IsDestinationSweepEnabled = destination is not null, IsLightningEnabled = IsArkadeLightningEnabled() });
    }

    [HttpGet("stores/{storeId}/spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SpendOverview(CancellationToken token)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup");

        if (!config.GeneratedByStore)
            return RedirectToAction("StoreOverview");

        return View(new SpendOverviewViewModel
        {
            AvailableBalance = await arkWalletService.GetBalanceInSats(config.WalletId, cancellation: token)
        });
    }

    [HttpPost("stores/{storeId}/spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SpendOverview(SpendOverviewViewModel model, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(model.Destination))
            return BadRequest();

        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup");

        if (!config.GeneratedByStore)
            return RedirectToAction("StoreOverview");

        var signer = await walletSignerProvider.GetSigner(config.WalletId, HttpContext.RequestAborted);
        if (signer is null)
            return NotFound();

        ArkOperatorTerms terms;
        try
        {
            terms = await operatorTermsService.GetOperatorTerms(HttpContext.RequestAborted);
        }
        catch (Exception e)
        {
            return NotFound();
        }


        if (model.Destination.Replace("lightning:", "", StringComparison.InvariantCultureIgnoreCase) is { } lnbolt11 &&
            BOLT11PaymentRequest.TryParse(lnbolt11, out var bolt11, terms.Network))
        {
            if (bolt11 is null)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: malfomed destination!";
                return RedirectToAction(nameof(SpendOverview), new { storeId = store.Id });
            }

            var lnConfig =
                store
                    .GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                        GetLightningPaymentMethod(),
                        paymentMethodHandlerDictionary
                    );

            if (lnConfig is null)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: lightning compatiblity is not enabled!";
                return RedirectToAction(nameof(SpendOverview), new { storeId = store.Id });
            }

            var lnClient = paymentMethodHandlerDictionary.GetLightningHandler("BTC").CreateLightningClient(lnConfig);
            var resp = await lnClient.Pay(bolt11.ToString(), HttpContext.RequestAborted);
            if (resp.Result == PayResult.Ok)
            {
                TempData[WellKnownTempData.SuccessMessage] = $"Payment sent to {bolt11}";
                return RedirectToAction(nameof(SpendOverview), new { storeId = store.Id });
            }

            TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: {resp?.Details?.Status}";
        }
        else if (Uri.TryCreate(model.Destination, UriKind.Absolute, out var uri) && uri.Scheme.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase))
        {
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0]; // uri.Host is empty so we must parse it ourselves

            var qs = uri.ParseQueryString();
            if (ArkAddress.TryParse(host, out var address) ||
                (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out address)))
            {
                if (address is null)
                {
                    TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: malformed destination!";
                    return RedirectToAction(nameof(SpendOverview), new { storeId = store.Id });
                }

                var amount = decimal.Parse(qs["amount"] ?? "0", CultureInfo.InvariantCulture);

                try
                {
                    var txId = await arkadeSpender.Spend(config.WalletId, [new TxOut(Money.Coins(amount), address)],
                        HttpContext.RequestAborted);

                    await arkWalletService.UpdateBalances(config.WalletId, true, token);
                    TempData[WellKnownTempData.SuccessMessage] = $"Payment sent to {address.ToString(terms.Network.ChainName == ChainName.Mainnet)} with txid {txId}";
                }
                catch (Exception e)
                {
                    TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: {e.Message}";
                }
            }
        }
        else
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: unsupported destination!";
        }

        return RedirectToAction(nameof(SpendOverview), new {storeId = store.Id});
    }


    [HttpGet("stores/{storeId}/contracts")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Contracts(StoreContractsViewModel? model = null)
    {
        model ??= new StoreContractsViewModel() { Skip = 0 };

        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup");

        if (!config.GeneratedByStore)
            return View(new StoreContractsViewModel());

        var contracts = await arkWalletService.GetArkWalletContractsAsync(config.WalletId, model.Skip, model.Count);

        model.Contracts = contracts;

        return View(model);
    }

    [HttpPost("stores/{storeId}/enable-ln")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EnableLightning(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        var lnConfig = new LightningPaymentMethodConfig()
        {
            ConnectionString = $"type=arkade;wallet-id={config.WalletId}",
        };
        store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[GetLightningPaymentMethod()], lnConfig);
        var blob = store.GetStoreBlob();
        blob.SetExcluded(GetLightningPaymentMethod(), false);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        TempData[WellKnownTempData.SuccessMessage] = "Lightning enabled";
        return RedirectToAction("StoreOverview", new { storeId });

    }

    private bool IsArkadeLightningEnabled()
    {
        var store = HttpContext.GetStoreData();
        var lnConfig =
            store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(GetLightningPaymentMethod(), paymentMethodHandlerDictionary);
        var lnEnabled =
            lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;
        return lnEnabled;
    }

    private async Task<TemporaryWalletSettings> GetFromInputWallet(string? wallet)
    {
        if (string.IsNullOrWhiteSpace(wallet))
            return new TemporaryWalletSettings(GenerateWallet(), null, null, true);

        if (wallet.StartsWith("nsec", StringComparison.InvariantCultureIgnoreCase))
        {
            return new TemporaryWalletSettings(wallet, null, null, true);
        }

        if (ArkAddress.TryParse(wallet, out var addr))
        {
            var terms = await operatorTermsService.GetOperatorTerms();

            if (!terms.SignerKey.ToBytes().SequenceEqual(addr!.ServerKey.ToBytes()))
                throw new Exception("Invalid destination address");

            return new TemporaryWalletSettings(GenerateWallet(), null, wallet, true);
        }

        if (HexEncoder.IsWellFormed(wallet) &&
            Encoders.Hex.DecodeData(wallet) is
            { Length: 32 } potentialwalletBytes &&
            ECXOnlyPubKey.TryCreate(potentialwalletBytes, out _))
        {
            if (!await arkWalletService.WalletExists(wallet, HttpContext.RequestAborted))
                throw new Exception("Unsupported value.");

            return new TemporaryWalletSettings(null, wallet, null, false);
        }

        throw new Exception("Unsupported value.");
    }
    private static string GenerateWallet()
    {
        var key = RandomUtils.GetBytes(32)!;
        var encoder = Encoders.Bech32("nsec");
        encoder.SquashBytes = true;
        encoder.StrictLength = false;
        var nsec = encoder.EncodeData(key, Bech32EncodingType.BECH32);
        return nsec;
    }

    private static PaymentMethodId GetLightningPaymentMethod() => PaymentTypes.LN.GetPaymentMethodId("BTC");

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }

    private record TemporaryWalletSettings(string? Wallet, string? WalletId, string? Destination, bool IsOwnedByStore);
}

