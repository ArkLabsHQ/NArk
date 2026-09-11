using System.ComponentModel.DataAnnotations;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

/// <summary>Owner-authorized public recovery journal; no route execution or secret endpoints.</summary>
/// <param name="repository">Store-scoped route persistence.</param>
[ApiController]
[Authorize(Policy = Policies.CanViewInvoices, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public sealed class ArkCompositionRoutesController(ArkInvoiceCompositionRepository repository) : ControllerBase
{
    /// <summary>Reads one route without revealing whether another store owns it.</summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/evm-settlement/routes/{routeId:guid}")]
    public async Task<IActionResult> Get(string storeId, Guid routeId, CancellationToken cancellationToken)
    {
        if (!OwnsStore(storeId)) return NotFound();
        var route = await repository.Get(storeId, routeId, cancellationToken);
        return route is null ? NotFound() : Ok(ArkCompositionRouteData.From(route));
    }

    /// <summary>Lists bounded independent routes, including unattached prompts and renewals.</summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/evm-settlement/routes")]
    public async Task<IActionResult> List(string storeId, CancellationToken cancellationToken,
        [FromQuery, StringLength(128)] string? invoiceId = null, [FromQuery] string? paymentMethodId = null,
        [FromQuery, Range(0, int.MaxValue)] int skip = 0, [FromQuery, Range(1, 100)] int take = 50)
    {
        if (!OwnsStore(storeId)) return NotFound();
        if (paymentMethodId is not (null or "ARKADE" or "BTC-LN" or "BTC-CHAIN"))
            return BadRequest(new { code = "invalid-payment-method", message = "Specify a supported source payment method." });
        var routes = await repository.List(storeId, invoiceId, paymentMethodId, skip, take, cancellationToken);
        return Ok(routes.Select(ArkCompositionRouteData.From).ToArray());
    }

    private bool OwnsStore(string storeId) => HttpContext.GetStoreDataOrNull() is { } store &&
                                             string.Equals(store.Id, storeId, StringComparison.Ordinal);
}
