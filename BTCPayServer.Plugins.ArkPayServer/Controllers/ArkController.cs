using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public class ArkStoreWalletViewModel
{
    public string? Wallet { get; set; }

    public bool SignerAvailable { get; set; }
    public Dictionary<ArkWalletContract, VTXO[]> Contracts { get; set; } = new();
    public bool LNEnabled { get; set; }
    
    
}


[Route("plugins/ark")]
[Authorize( AuthenticationSchemes = AuthenticationSchemes.Cookie)]

public class ArkController : Controller
{
    private readonly StoreRepository _storeRepository;
    private readonly ArkWalletService _arkWalletService;
    private readonly ArkadeWalletSignerProvider _walletSignerProvider;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;

    public ArkController(
        StoreRepository storeRepository,
        ArkWalletService arkWalletService, 
        ArkadeWalletSignerProvider walletSignerProvider,
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary)
    {
        _storeRepository = storeRepository;
        _arkWalletService = arkWalletService;
        _walletSignerProvider = walletSignerProvider;
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
        if (config?.WalletId == null)
        {
            return View(new ArkStoreWalletViewModel());
        }
        
        var walletInfo = await _arkWalletService.GetWalletInfo(config.WalletId);
        if (walletInfo is null)
        {
            
            TempData[WellKnownTempData.ErrorMessage] = "Wallet not found in records anymore. Please re-import the wallet.";
            return await SetupStore(storeId, new ArkStoreWalletViewModel());
        }

        var lnPmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig =store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lnPmi, _paymentMethodHandlerDictionary);
       
        return View(new ArkStoreWalletViewModel()
        {
            Wallet = config.WalletId,
            SignerAvailable = await _walletSignerProvider.GetSigner(config.WalletId, HttpContext.RequestAborted) is not null,
            Contracts = walletInfo,
            LNEnabled = lnConfig?.ConnectionString?.StartsWith("type=arkade") is true
        });
    }

    [HttpPost("stores/{storeId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SetupStore(string storeId, ArkStoreWalletViewModel model, string? command = null)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var generatedKey = "";
        
        var lnPmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig =store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lnPmi, _paymentMethodHandlerDictionary);

        var lnEnabled = lnConfig?.ConnectionString?.StartsWith("type=arkade") is true;
        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (command == "create")
        {
            var key = RandomUtils.GetBytes(32)!;
            var encoder = Encoders.Bech32("nsec");
            encoder.SquashBytes = true;
            encoder.StrictLength = false;
            var npub = encoder.EncodeData(key, Bech32EncodingType.BECH32);
            model.Wallet = npub;
            generatedKey = "Your new wallet key is: " + npub;
            
        }else if (command == "enable-ln" && config?.WalletId != null)
        {
            lnConfig = new LightningPaymentMethodConfig()
            {
                ConnectionString = $"type=arkade;wallet-id={config.WalletId}",

            };
            store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[lnPmi], lnConfig);
            await _storeRepository.UpdateStore(store);
            TempData[WellKnownTempData.SuccessMessage] = "Lightning enabled";
            return RedirectToAction(nameof(SetupStore), new { storeId });
            
        }else if (command == "poll-scripts" && config?.WalletId != null)
        {
            await _arkWalletService.UpdateBalances(config.WalletId, true);
            TempData[WellKnownTempData.SuccessMessage] = "Scripts polled";
            return RedirectToAction(nameof(SetupStore), new { storeId });
        }else if (command == "clear" && config?.WalletId != null)
        {
            model.Wallet = null;
        }


        if (model.Wallet != config?.WalletId )
        {
            if (string.IsNullOrEmpty(model.Wallet))
            {
                store.SetPaymentMethodConfig(ArkadePlugin.ArkadePaymentMethodId, null);
                if (lnEnabled)
                {
                    store.SetPaymentMethodConfig(lnPmi, null);
                }
            }else
            {
                ArkWallet wallet;
                try
                {
                    wallet = await _arkWalletService.Upsert(model.Wallet);
                }catch (Exception ex)
                {
                    
                    TempData[WellKnownTempData.ErrorMessage] = "Could not update wallet: " + ex.Message;
                    return View(model);
                }

                config = new ArkadePaymentMethodConfig(wallet.Id);
                store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], config);
                if (lnEnabled || lnConfig is null)
                {
                    lnConfig = new LightningPaymentMethodConfig()
                    {
                        ConnectionString = $"type=arkade;wallet-id={wallet.Id}",

                    };
                    store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[lnPmi], lnConfig);
                }
            }
            
        }
        await _storeRepository.UpdateStore(store);
        if (string.IsNullOrEmpty(generatedKey))
        {
            TempData[WellKnownTempData.SuccessMessage] = $"Ark Payment method successfully {(string.IsNullOrEmpty(config?.WalletId) ? "enabled" : "updated")}.";

        }
        else
        {
            TempData[WellKnownTempData.SuccessMessage] = $"Ark Payment method successfully generated. {generatedKey}";
        }
       
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
    //

    
    // [HttpGet("wallet/{walletId}")]
    // public async Task<IActionResult> WalletDetails(string walletId)
    // {
    //     var wallet = await _arkWalletService.GetWalletAsync(walletId);
    //     if (wallet == null)
    //     {
    //         return NotFound();
    //     }
    //
    //     ViewData["Title"] = "Wallet Details";
    //     return View(wallet);
    // }

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
