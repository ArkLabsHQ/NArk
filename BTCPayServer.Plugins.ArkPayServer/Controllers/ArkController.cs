
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk;
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
    ArkadeWalletSignerProvider walletSignerProvider,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    IOperatorTermsService operatorTermsService,
    IAuthorizationService authorizationService,
    ArkadeSpender arkadeSpender) : Controller
{
    [HttpGet("stores/{storeId}/initial-setup")]
    public IActionResult InitialSetup(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId == null)
        {
            return View(new ArkStoreWalletViewModel());
        }

        return RedirectToAction("StoreOverview", new { storeId });
    }

    [HttpPost("stores/{storeId}/initial-setup")]
    public async Task<IActionResult> InitialSetup(string storeId, InitialWalletSetupViewModel model)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        try
        {
            var walletSettings = await GetFromInputWallet(model.Wallet);

            try
            {
                walletSettings.WalletId =
                    await arkWalletService.Upsert(
                        model.Wallet,
                        walletSettings.Destination,
                        walletSettings.IsOwnedByStore,
                        HttpContext.RequestAborted
                    );
            }
            catch (Exception ex)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Could not update wallet: " + ex.Message;
                return View(model);
            }
            
            var config = new ArkadePaymentMethodConfig(walletSettings.WalletId, walletSettings.IsOwnedByStore);
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

    private async Task<TemporaryWalletSettings> GetFromInputWallet(string wallet)
    {
        if (string.IsNullOrWhiteSpace(wallet))
            return new TemporaryWalletSettings(GenerateWallet(), null, null, true);

        if (wallet.StartsWith("nsec", StringComparison.InvariantCultureIgnoreCase))
        {
            //TODO: more validation
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

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }
}

internal record struct TemporaryWalletSettings(string? Wallet, string? WalletId, string? Destination, bool IsOwnedByStore);