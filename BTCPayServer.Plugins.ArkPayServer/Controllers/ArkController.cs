
using System.Globalization;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.PayoutProcessors.Lightning;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
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
    ArkadeSpendingService arkadeSpendingService,
    ArkAutomatedPayoutSenderFactory payoutSenderFactory,
    PayoutProcessorService payoutProcessorService,
    EventAggregator eventAggregator) : Controller
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

            TempData[WellKnownTempData.SuccessMessage] = "Ark Payment method updated.";

            return RedirectToAction(nameof(InitialSetup), new { storeId });
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
            return RedirectToAction(nameof(InitialSetup), new { storeId = store.Id });

        var destination = arkWalletService.GetWalletDestination(config.WalletId);

        return View(new StoreOverviewViewModel { IsDestinationSweepEnabled = destination is not null, IsLightningEnabled = IsArkadeLightningEnabled() });
    }

    [HttpGet("stores/{storeId}/spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SpendOverview(string[]? destinations, CancellationToken token)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction(nameof(InitialSetup), new { storeId = store.Id });

        if (!config.GeneratedByStore)
            return RedirectToAction(nameof(StoreOverview), new { storeId = store.Id });

        return View(new SpendOverviewViewModel
        {
            PrefilledDestination = destinations?.ToList() ?? [],
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

        try
        {
            var maybeProof = await arkadeSpendingService.Spend(store, model.Destination, token);
            TempData[WellKnownTempData.SuccessMessage] = $"Payment sent to {model.Destination}";
            model.PrefilledDestination.Remove(model.Destination);
            return RedirectToAction(nameof(SpendOverview),
                new { storeId = store.Id, destinations = model.PrefilledDestination });
        }
        catch (IncompleteArkadeSetupException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Payment failed: incomplete arkade setup!";
            return RedirectToAction(nameof(InitialSetup), new { storeId = store.Id });
        }
        catch (MalformedPaymentDestination e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Payment failed: malfomed destination!";
            return RedirectToAction(nameof(SpendOverview),
                new { storeId = store.Id, destinations = model.PrefilledDestination });
        }
        catch (ArkadePaymentFailedException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Payment failed: reason: {e.Message}";
            return RedirectToAction(nameof(SpendOverview),
                new { storeId = store.Id, destinations = model.PrefilledDestination });
        }
    }


    [HttpGet("stores/{storeId}/contracts")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Contracts(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50,
        bool loadVtxos = false,
        bool includeSpent = false,
        bool includeRecoverable = false)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            return RedirectToAction("InitialSetup", new { storeId });

        if (!config.GeneratedByStore)
            return View(new StoreContractsViewModel { StoreId = storeId });

        // Get status filter
        bool? activeFilter = null;
        if (new SearchString(searchTerm).ContainsFilter("status"))
        {
            var statusFilters = new SearchString(searchTerm).GetFilterArray("status");
            if (statusFilters.Length == 1)
            {
                activeFilter = statusFilters[0] == "active";
            }
        }

        var (contracts, contractVtxos) = await arkWalletService.GetArkWalletContractsAsync(
            config.WalletId, 
            skip, 
            count, 
            searchText ?? "", 
            activeFilter,
            loadVtxos,
            allowSpent: includeSpent,
            allowNote: includeRecoverable,
            HttpContext.RequestAborted);

        var model = new StoreContractsViewModel
        {
            StoreId = storeId,
            Contracts = contracts,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            LoadVtxos = loadVtxos,
            IncludeSpent = includeSpent,
            IncludeRecoverable = includeRecoverable,
            ContractVtxos = contractVtxos
        };

        return View(model);
    }

    [HttpGet("stores/{storeId}/enable-ln")]
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
            { Length: 32 } potentialWalletBytes &&
            ECXOnlyPubKey.TryCreate(potentialWalletBytes, out _))
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

    [HttpGet("~/stores/{storeId}/payout-processors/ark-automated")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ConfigurePayoutProcessor(string storeId)
    {
        var activeProcessor =
            (await payoutProcessorService.GetProcessors(
                new PayoutProcessorService.PayoutProcessorQuery()
                {
                    Stores = new[] { storeId },
                    Processors = new[] { payoutSenderFactory.Processor },
                    PayoutMethods = new[]
                    {
                        ArkadePlugin.ArkadePayoutMethodId
                    }
                }))
            .FirstOrDefault();

        return View(new ConfigureArkPayoutProcessorViewModel(activeProcessor is null ? new ArkAutomatedPayoutBlob() : ArkAutomatedPayoutProcessor.GetBlob(activeProcessor)));
    }
    
    [HttpPost("~/stores/{storeId}/payout-processors/ark-automated/")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ConfigurePayoutProcessor(string storeId, ConfigureArkPayoutProcessorViewModel automatedTransferBlob)
    {
        if (!ModelState.IsValid)
            return View(automatedTransferBlob);
        
        var activeProcessor =
            (await payoutProcessorService.GetProcessors(
                new PayoutProcessorService.PayoutProcessorQuery()
                {
                    Stores = [storeId],
                    Processors = [payoutSenderFactory.Processor],
                    PayoutMethods =
                    [
                        ArkadePlugin.ArkadePayoutMethodId
                    ]
                }))
            .FirstOrDefault();
        activeProcessor ??= new PayoutProcessorData();
        activeProcessor.HasTypedBlob<ArkAutomatedPayoutBlob>().SetBlob(automatedTransferBlob.ToBlob());
        activeProcessor.StoreId = storeId;
        activeProcessor.PayoutMethodId = ArkadePlugin.ArkadePayoutMethodId.ToString();
        activeProcessor.Processor = payoutSenderFactory.Processor;
        var tcs = new TaskCompletionSource();
        eventAggregator.Publish(new PayoutProcessorUpdated()
        {
            Data = activeProcessor,
            Id = activeProcessor.Id,
            Processed = tcs
        });
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "Processor updated."
        });
        await tcs.Task;
        return RedirectToAction(nameof(ConfigurePayoutProcessor), "Ark", new { storeId });
    }
}

