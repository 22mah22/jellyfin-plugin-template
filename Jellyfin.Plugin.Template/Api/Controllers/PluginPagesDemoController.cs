using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Template.Api.Controllers;

/// <summary>
/// Demo endpoint intended for integration with the Plugin Pages sidebar plugin.
/// </summary>
[ApiController]
[Authorize]
[Route("PluginTemplate/Demo")]
public class PluginPagesDemoController : ControllerBase
{
    /// <summary>
    /// Returns a minimal hello-world HTML fragment for Plugin Pages.
    /// </summary>
    /// <returns>Static hello-world html.</returns>
    [HttpGet("Hello")]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<string> Hello()
    {
        const string html = "<h1>Hello world</h1><p>This page is served by Jellyfin.Plugin.Template.</p>";
        return Content(html, "text/html");
    }
}
