using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Plugins.ArkPayServer.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

[Route("plugins/ark")]
public class ArkController : Controller
{
    private readonly ArkWalletService _arkWalletService;

    public ArkController(ArkWalletService arkWalletService)
    {
        _arkWalletService = arkWalletService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Ark Pay Server";
        
        var wallets = await _arkWalletService.GetAllWalletsAsync();
        var model = new ArkIndexViewModel
        {
            Wallets = wallets
        };
        
        return View(model);
    }

    [HttpGet("create-wallet")]
    public async Task<IActionResult> CreateWallet()
    {
        ViewData["Title"] = "Create New Ark Wallet";
        
        return View(new CreateWalletViewModel());
    }

    [HttpPost("create-wallet")]
    public async Task<IActionResult> CreateWallet(CreateWalletViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var request = new WalletCreationRequest(model.PubKey);
            var wallet = await _arkWalletService.CreateNewWalletAsync(request);
            TempData["StatusMessage"] = "Ark wallet created successfully!";
            return RedirectToAction(nameof(WalletDetails), new { walletId = wallet.Id });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", $"Failed to create wallet: {ex.Message}");
            return View(model);
        }
    }

    [HttpGet("wallet/{walletId:guid}")]
    public async Task<IActionResult> WalletDetails(Guid walletId)
    {
        var wallet = await _arkWalletService.GetWalletAsync(walletId);
        if (wallet == null)
        {
            return NotFound();
        }

        ViewData["Title"] = "Wallet Details";
        return View(wallet);
    }

    [HttpGet("wallet/{walletId:guid}/boarding-addresses")]
    public async Task<IActionResult> BoardingAddresses(Guid walletId)
    {
        var wallet = await _arkWalletService.GetWalletAsync(walletId);
        if (wallet == null)
        {
            return NotFound();
        }

        var boardingAddresses = await _arkWalletService.GetBoardingAddressesAsync(walletId);
        
        ViewData["Title"] = "Boarding Addresses";
        ViewData["WalletId"] = walletId;
        ViewData["WalletName"] = $"Wallet {wallet.Id:N}";
        
        return View(boardingAddresses);
    }

    [HttpPost("wallet/{walletId:guid}/create-boarding-address")]
    public async Task<IActionResult> CreateBoardingAddress(Guid walletId)
    {
        var wallet = await _arkWalletService.GetWalletAsync(walletId);
        if (wallet == null)
        {
            return NotFound();
        }

        try
        {
            var boardingAddress = await _arkWalletService.DeriveNewBoardingAddress(walletId);
                
            TempData["StatusMessage"] = $"Boarding address created successfully: {boardingAddress.OnchainAddress}";
            return RedirectToAction(nameof(BoardingAddresses), new { walletId });
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Failed to create boarding address: {ex.Message}";
            return RedirectToAction(nameof(BoardingAddresses), new { walletId });
        }
    }
}
