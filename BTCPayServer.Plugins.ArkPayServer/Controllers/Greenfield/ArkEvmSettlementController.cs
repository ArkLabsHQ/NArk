using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Wallets;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class ArkEvmSettlementController(IArkEvmSettlementStore settlementStore, IWalletProvider walletProvider) : ControllerBase
{
    [HttpGet("~/api/v1/stores/{storeId}/arkade/evm-settlement")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult GetConfiguration(string storeId)
    {
        var store = OwnedStore(storeId);
        if (store is null) return NotFound();
        var settings = settlementStore.GetConfiguration(store)?.EvmSettlement;
        return settings is null ? NoContent() : Ok(settings);
    }

    [HttpPut("~/api/v1/stores/{storeId}/arkade/evm-settlement")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> SetConfiguration(string storeId, [FromBody] ArkEvmSettlementSettings settings,
        CancellationToken cancellationToken)
    {
        var store = OwnedStore(storeId);
        if (store is null) return NotFound();
        var configuration = settlementStore.GetConfiguration(store);
        if (string.IsNullOrWhiteSpace(configuration?.WalletId))
            return Conflict(new { code = "arkade-not-configured", message = "Configure an Arkade wallet for this store first." });
        try
        {
            settings = settings.Validate();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { code = "invalid-settlement-settings", message = "Specify a valid EVM asset and destination." });
        }

        cancellationToken.ThrowIfCancellationRequested();
        await settlementStore.SaveAsync(store, configuration with { EvmSettlement = settings });
        return Ok(settings);
    }

    [HttpGet("~/api/v1/stores/{storeId}/arkade/evm-settlement/capabilities")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetCapabilities(string storeId, CancellationToken cancellationToken)
    {
        var store = OwnedStore(storeId);
        if (store is null) return NotFound();
        var configuration = settlementStore.GetConfiguration(store);
        var walletConfigured = !string.IsNullOrWhiteSpace(configuration?.WalletId);
        var signerAvailable = walletConfigured &&
                              await walletProvider.GetSignerAsync(configuration!.WalletId, cancellationToken) is not null;
        return Ok(new ArkEvmSettlementCapabilitiesData(walletConfigured, signerAvailable,
            configuration?.EvmSettlement?.Enabled == true));
    }

    private StoreData? OwnedStore(string storeId) =>
        HttpContext.GetStoreDataOrNull() is { } store && string.Equals(store.Id, storeId, StringComparison.Ordinal)
            ? store : null;
}
