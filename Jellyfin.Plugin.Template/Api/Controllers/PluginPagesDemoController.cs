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
        const string html = """
                            <!doctype html>
                            <html lang="en">
                            <head>
                                <meta charset="utf-8">
                                <meta name="viewport" content="width=device-width,initial-scale=1">
                                <title>GIF Generator</title>
                                <style>
                                    html, body {
                                        margin: 0;
                                        padding: 0;
                                        width: 100%;
                                        height: 100%;
                                        background: #101214;
                                        color: #fff;
                                        font-family: sans-serif;
                                    }

                                    .frame {
                                        border: 0;
                                        width: 100%;
                                        height: 100%;
                                    }
                                </style>
                            </head>
                            <body>
                                <iframe id="GifGeneratorFrame" class="frame" title="GIF Generator"></iframe>
                                <script>
                                    (function () {
                                        var frame = document.getElementById('GifGeneratorFrame');
                                        var basePath = window.location.pathname.replace(/\/PluginTemplate\/Demo\/Hello\/?$/, '');
                                        frame.src = basePath + '/web/index.html#!/gif-generator';
                                    })();
                                </script>
                            </body>
                            </html>
                            """;
        return Content(html, "text/html");
    }
}
