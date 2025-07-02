using System.Text;
using System.Xml;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public class ArkStoreWalletViewModel
{
    public string? Wallet { get; set; }
    // public Dictionary<string, string> Wallets { get; set; }
}


[Route("plugins/ark")]
[Authorize( AuthenticationSchemes = AuthenticationSchemes.Cookie)]

public class ArkController : Controller
{
    private readonly StoreRepository _storeRepository;
    private readonly ArkWalletService _arkWalletService;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;

    public ArkController(
        StoreRepository storeRepository,
        ArkWalletService arkWalletService, 
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary)
    {
        _storeRepository = storeRepository;
        _arkWalletService = arkWalletService;
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
    }

    [HttpGet("stores/{storeId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SetupStore(string storeId)
    {  
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();
        
        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        // var availableWallets = await _arkWalletService.GetAllWalletsAsync();
        return View(new ArkStoreWalletViewModel()
        {
            Wallet = config?.WalletId,
        });


    }

    [HttpPost("stores/{storeId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SetupStore(string storeId, ArkStoreWalletViewModel model, string? action = null)
    {
        
        
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        if (action == "create")
        {
            var key = RandomUtils.GetBytes(32)!;
            var privKey = ECPrivKey.Create(key);
            var pubKey = privKey.CreateXOnlyPubKey();
            var encoder = Encoders.Bech32("npub");
            encoder.SquashBytes = true;
            encoder.StrictLength = false;
            var npub = encoder.EncodeData(pubKey.ToBytes(), Bech32EncodingType.BECH32);
            model.Wallet = npub;
        }
        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (model.Wallet != config?.WalletId )
        {
            if (string.IsNullOrEmpty(model.Wallet))
            {
                store.SetPaymentMethodConfig(ArkadePlugin.ArkadePaymentMethodId, null);
            }else
            {
                var wallet = await _arkWalletService.Upsert(model.Wallet);
                config = new ArkadePaymentMethodConfig(wallet.Id);
                store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], config);
            }
            
        }
        await _storeRepository.UpdateStore(store);
        TempData[WellKnownTempData.SuccessMessage] = $"Ark Payment method successfully {(string.IsNullOrEmpty(config?.WalletId) ? "enabled" : "updated")}.";

        return RedirectToAction("SetupStore", new { storeId });
    }
    
    

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T: class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, _paymentMethodHandlerDictionary);
    }
    
    // [HttpGet("")]
    // public async Task<IActionResult> Index()
    // {
    //     ViewData["Title"] = "Ark Pay Server";
    //     
    //     var wallets = await _arkWalletService.GetAllWalletsAsync();
    //     var model = new ArkIndexViewModel
    //     {
    //         Wallets = wallets
    //     };
    //     
    //     return View(model);
    // }

    // [HttpGet("create-wallet")]
    // public async Task<IActionResult> CreateWallet()
    // {
    //     ViewData["Title"] = "Create New Ark Wallet";
    //     
    //     return View(new CreateWalletViewModel());
    // }
    //
    // [HttpPost("create-wallet")]
    // public async Task<IActionResult> CreateWallet(CreateWalletViewModel model)
    // {
    //     
    //     
    //     if (!ModelState.IsValid)
    //     {
    //         return View(model);
    //     }
    //
    //     try
    //     {
    //         var wallet = await _arkWalletService.CreateNewWalletAsync(model.Wallet);
    //         TempData["StatusMessage"] = "Ark wallet created successfully!";
    //         return RedirectToAction(nameof(WalletDetails), new { walletId = wallet.Id });
    //     }
    //     catch (Exception ex)
    //     {
    //         ModelState.AddModelError("", $"Failed to create wallet: {ex.Message}");
    //         return View(model);
    //     }
    // }

    [HttpGet("wallet/{walletId}")]
    public async Task<IActionResult> WalletDetails(string walletId)
    {
        var wallet = await _arkWalletService.GetWalletAsync(walletId);
        if (wallet == null)
        {
            return NotFound();
        }

        ViewData["Title"] = "Wallet Details";
        return View(wallet);
    }

    // [HttpGet("wallet/{walletId:guid}/boarding-addresses")]
    // public async Task<IActionResult> BoardingAddresses(Guid walletId)
    // {
    //     var wallet = await _arkWalletService.GetWalletAsync(walletId);
    //     if (wallet == null)
    //     {
    //         return NotFound();
    //     }
    //
    //     var boardingAddresses = await _arkWalletService.GetBoardingAddressesAsync(walletId);
    //     
    //     ViewData["Title"] = "Boarding Addresses";
    //     ViewData["WalletId"] = walletId;
    //     ViewData["WalletName"] = $"Wallet {wallet.Id:N}";
    //     
    //     return View(boardingAddresses);
    // }
    //
    // [HttpPost("wallet/{walletId:guid}/create-boarding-address")]
    // public async Task<IActionResult> CreateBoardingAddress(Guid walletId)
    // {
    //     var wallet = await _arkWalletService.GetWalletAsync(walletId);
    //     if (wallet == null)
    //     {
    //         return NotFound();
    //     }
    //
    //     try
    //     {
    //         var boardingAddress = await _arkWalletService.DeriveNewBoardingAddress(walletId);
    //             
    //         TempData["StatusMessage"] = $"Boarding address created successfully: {boardingAddress.OnchainAddress}";
    //         return RedirectToAction(nameof(BoardingAddresses), new { walletId });
    //     }
    //     catch (Exception ex)
    //     {
    //         TempData["ErrorMessage"] = $"Failed to create boarding address: {ex.Message}";
    //         return RedirectToAction(nameof(BoardingAddresses), new { walletId });
    //     }
    // }
}
