using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Abstractions.Extensions;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

[Route("plugins/ark")]
public class ArkController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        ViewData["Title"] = "Ark Pay Server";
        return View();
    }
}
