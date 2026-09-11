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
public class ArkEvmSettlementController(IArkEvmSettlementStore settlementStore, IWalletProvider walletProvider,
    ArkEvmRpcEndpointProtector rpcProtector) : ControllerBase
{
    [HttpGet("~/api/v1/stores/{storeId}/arkade/evm-settlement")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult GetConfiguration(string storeId)
    {
        var store = OwnedStore(storeId);
        if (store is null) return NotFound();
        var configuration = settlementStore.GetConfiguration(store);
        var settings = configuration?.EvmSettlement;
        return settings is null ? NoContent() : Ok(ToData(storeId, settings, !string.IsNullOrWhiteSpace(configuration?.WalletId)));
    }

    [HttpPut("~/api/v1/stores/{storeId}/arkade/evm-settlement")]
    [ArkEvmSettlementValidation]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> SetConfiguration(string storeId,
        [FromBody, ModelBinder(BinderType = typeof(ArkEvmSettlementUpdateBinder))] ArkEvmSettlementUpdateData update,
        CancellationToken cancellationToken)
    {
        var store = OwnedStore(storeId);
        if (store is null) return NotFound();
        var configuration = settlementStore.GetConfiguration(store);
        if (string.IsNullOrWhiteSpace(configuration?.WalletId))
            return Conflict(new { code = "arkade-not-configured", message = "Configure an Arkade wallet for this store first." });
        ArkEvmSettlementSettings settings;
        try
        {
            var rpc = update.RpcEndpoint;
            var protectedRpc = rpc switch
            {
                null => configuration.EvmSettlement?.ProtectedRpcUri,
                { Action: "preserve", Uri: null } => configuration.EvmSettlement?.ProtectedRpcUri,
                { Action: "clear", Uri: null } => null,
                { Action: "replace" } => rpcProtector.Protect(storeId, rpc.Uri),
                _ => throw new ArgumentException("Specify a valid RPC endpoint update.")
            };
            settings = new ArkEvmSettlementSettings(update.AssetId, update.Destination, update.Enabled)
            {
                RoutePolicy = update.RoutePolicy,
                ProtectedRpcUri = protectedRpc
            }.Validate();
            if (settings.Enabled && !ToData(storeId, settings).ConfigurationComplete)
                throw new ArgumentException("Enabled settlement requires a complete route policy and RPC endpoint.");
        }
        catch (ArgumentException)
        {
            return ArkEvmSettlementValidationAttribute.InvalidSettings();
        }

        cancellationToken.ThrowIfCancellationRequested();
        await settlementStore.SaveAsync(store, configuration with { EvmSettlement = settings });
        return Ok(ToData(storeId, settings));
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
        var settings = configuration?.EvmSettlement;
        var data = settings is null ? null : ToData(storeId, settings, walletConfigured);
        return Ok(new ArkEvmSettlementCapabilitiesData(walletConfigured, signerAvailable, settings?.Enabled == true,
            data?.ConfigurationComplete == true, settings?.RoutePolicy?.EnabledSourceRails ?? [],
            data?.RpcEndpointConfigured == true, data?.RpcEndpointOrigin,
            data?.MissingConfiguration ?? (walletConfigured ? ["settlement-settings-missing"] :
                ["arkade-wallet-missing", "settlement-settings-missing"])));
    }

    private ArkEvmSettlementData ToData(string storeId, ArkEvmSettlementSettings settings, bool walletConfigured = true)
    {
        var rpc = rpcProtector.TryUnprotect(storeId, settings.ProtectedRpcUri);
        var missing = new List<string>();
        if (!walletConfigured) missing.Add("arkade-wallet-missing");
        if (settings.RoutePolicy is null) missing.Add("route-policy-missing");
        if (rpc is null) missing.Add(settings.ProtectedRpcUri is null ? "rpc-endpoint-missing" : "rpc-endpoint-unavailable");
        var origin = rpc is null ? null : new UriBuilder(rpc.Scheme, rpc.Host, rpc.IsDefaultPort ? -1 : rpc.Port)
            .Uri.GetLeftPart(UriPartial.Authority);
        return new ArkEvmSettlementData(settings.AssetId, settings.Destination, settings.Enabled, settings.RoutePolicy,
            rpc is not null, origin, missing.ToArray());
    }

    private StoreData? OwnedStore(string storeId) =>
        HttpContext.GetStoreDataOrNull() is { } store && string.Equals(store.Id, storeId, StringComparison.Ordinal)
            ? store : null;
}
