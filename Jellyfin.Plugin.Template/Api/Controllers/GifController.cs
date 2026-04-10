using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Template.Api.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Template.Api.Controllers;

/// <summary>
/// API endpoints for authenticated gif generation.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/GifGenerator")]
public class GifController : ControllerBase
{
    private static readonly Regex SafeGifFileNamePattern = new(@"^[A-Za-z0-9_.-]+\.gif$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _serverApplicationPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="GifController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="serverApplicationPaths">The server application paths.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    public GifController(
        ILibraryManager libraryManager,
        IApplicationPaths serverApplicationPaths,
        IServerConfigurationManager serverConfigurationManager)
    {
        _libraryManager = libraryManager;
        _serverApplicationPaths = serverApplicationPaths;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <summary>
    /// Generates a gif from a local video file.
    /// </summary>
    /// <param name="request">The generation request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The generated gif metadata.</returns>
    [HttpPost("Create")]
    [ProducesResponseType<CreateGifResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<CreateGifResponse>> CreateGif([FromBody] CreateGifRequest request, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Plugin has not been initialized.");
        }

        if (request.LengthSeconds > plugin.Configuration.MaxGifLengthSeconds)
        {
            return BadRequest($"LengthSeconds cannot exceed {plugin.Configuration.MaxGifLengthSeconds} seconds.");
        }

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null || item.MediaType != MediaType.Video || string.IsNullOrWhiteSpace(item.Path))
        {
            return NotFound("Video item was not found or does not have a local file path.");
        }

        var ffmpegPath = _serverConfigurationManager.GetEncodingOptions().EncoderAppPath;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Jellyfin ffmpeg path is not configured.");
        }

        var outputDirectory = Path.Combine(_serverApplicationPaths.DataPath, "plugins", "gif-generator", "generated");
        Directory.CreateDirectory(outputDirectory);

        var outputFileName = $"{request.ItemId:N}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.gif";
        var outputPath = Path.Combine(outputDirectory, outputFileName);

        var fps = request.Fps > 0 ? request.Fps : plugin.Configuration.DefaultFps;
        var width = request.Width > 0 ? request.Width : plugin.Configuration.DefaultWidth;

        var processInfo = BuildProcessStartInfo(ffmpegPath, request.StartSeconds, request.LengthSeconds, item.Path, fps, width, outputPath);

        using var process = new Process { StartInfo = processInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"ffmpeg failed with exit code {process.ExitCode}. {stderr}");
        }

        return Ok(new CreateGifResponse
        {
            FileName = outputFileName,
            DownloadUrl = $"Plugins/GifGenerator/Download/{Uri.EscapeDataString(outputFileName)}"
        });
    }

    /// <summary>
    /// Downloads a generated gif.
    /// </summary>
    /// <param name="fileName">The gif file name.</param>
    /// <returns>A generated gif file.</returns>
    [HttpGet("Download/{fileName}")]
    [Produces("image/gif")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DownloadGif([FromRoute] string fileName)
    {
        var decodedFileName = Uri.UnescapeDataString(fileName);

        if (string.IsNullOrWhiteSpace(decodedFileName)
            || decodedFileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || !string.Equals(decodedFileName, Path.GetFileName(decodedFileName), StringComparison.Ordinal))
        {
            return NotFound();
        }

        if (!SafeGifFileNamePattern.IsMatch(decodedFileName))
        {
            return NotFound();
        }

        var outputDirectory = Path.Combine(_serverApplicationPaths.DataPath, "plugins", "gif-generator", "generated");
        if (!Directory.Exists(outputDirectory))
        {
            return NotFound();
        }

        var matchingGifPath = Directory
            .EnumerateFiles(outputDirectory, "*.gif", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), decodedFileName, StringComparison.Ordinal));

        if (string.IsNullOrEmpty(matchingGifPath))
        {
            return NotFound();
        }

        return PhysicalFile(matchingGifPath, "image/gif", enableRangeProcessing: true);
    }

    private static ProcessStartInfo BuildProcessStartInfo(
        string ffmpegPath,
        double startSeconds,
        double lengthSeconds,
        string inputPath,
        int fps,
        int width,
        string outputPath)
    {
        var start = startSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var length = lengthSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var videoFilter = BuildVideoFilter(fps, width);

        var processInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        processInfo.ArgumentList.Add("-hide_banner");
        processInfo.ArgumentList.Add("-loglevel");
        processInfo.ArgumentList.Add("error");
        processInfo.ArgumentList.Add("-ss");
        processInfo.ArgumentList.Add(start);
        processInfo.ArgumentList.Add("-t");
        processInfo.ArgumentList.Add(length);
        processInfo.ArgumentList.Add("-i");
        processInfo.ArgumentList.Add(inputPath);
        processInfo.ArgumentList.Add("-vf");
        processInfo.ArgumentList.Add(videoFilter);
        processInfo.ArgumentList.Add("-y");
        processInfo.ArgumentList.Add(outputPath);

        return processInfo;
    }

    private static string BuildVideoFilter(int fps, int width)
    {
        var builder = new StringBuilder();
        builder.Append("fps=");
        builder.Append(fps.ToString(CultureInfo.InvariantCulture));
        builder.Append(",scale=");
        builder.Append(width.ToString(CultureInfo.InvariantCulture));
        builder.Append(":-1:flags=lanczos");
        return builder.ToString();
    }
}
