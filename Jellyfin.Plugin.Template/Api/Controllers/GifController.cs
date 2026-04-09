using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Template.Api.Models;
using MediaBrowser.Common.Configuration;
using ApplicationPaths = MediaBrowser.Common.Configuration.IApplicationPaths;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    private static readonly string[] DockerAndLinuxFfmpegCandidates =
    [
        "/usr/lib/jellyfin-ffmpeg/ffmpeg",
        "/usr/lib/jellyfin-ffmpeg5/ffmpeg",
        "/usr/lib/jellyfin-ffmpeg6/ffmpeg",
        "/usr/lib/jellyfin-ffmpeg7/ffmpeg",
        "/usr/bin/ffmpeg"
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly ApplicationPaths _applicationPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="GifController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="applicationPaths">The server application paths.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    public GifController(
        ILibraryManager libraryManager,
        ApplicationPaths applicationPaths,
        IServerConfigurationManager serverConfigurationManager)
    {
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <summary>
    /// Generates a gif from a local video file.
    /// </summary>
    /// <param name="request">The generation request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The generated gif metadata.</returns>
    [HttpPost("Create")]
    [ProducesResponseType(typeof(CreateGifResponse), StatusCodes.Status200OK)]
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

        var ffmpegPath = ResolveFfmpegPath(_serverConfigurationManager.GetEncodingOptions().EncoderAppPath);
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                "No ffmpeg binary was found. Check Jellyfin Encoding settings (EncoderAppPath) or ensure jellyfin-ffmpeg exists in the container.");
        }

        var outputDirectory = Path.Combine(_applicationPaths.DataPath, "plugins", "gif-generator", "generated");
        Directory.CreateDirectory(outputDirectory);

        var outputFileName = $"{request.ItemId:N}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.gif";
        var outputPath = Path.Combine(outputDirectory, outputFileName);

        var fps = request.Fps > 0 ? request.Fps : plugin.Configuration.DefaultFps;
        var width = request.Width > 0 ? request.Width : plugin.Configuration.DefaultWidth;

        var ffmpegArguments = BuildArguments(request.StartSeconds, request.LengthSeconds, item.Path, fps, width, outputPath);
        var processInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = ffmpegArguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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
        var outputDirectory = Path.Combine(_applicationPaths.DataPath, "plugins", "gif-generator", "generated");
        var fullPath = Path.GetFullPath(Path.Combine(outputDirectory, decodedFileName));

        if (!fullPath.StartsWith(Path.GetFullPath(outputDirectory), StringComparison.Ordinal) || !System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        return PhysicalFile(fullPath, "image/gif", enableRangeProcessing: true);
    }

    private static string? ResolveFfmpegPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in DockerAndLinuxFfmpegCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var possibleBinary = Path.Combine(segment, "ffmpeg");
            if (File.Exists(possibleBinary))
            {
                return possibleBinary;
            }
        }

        return null;
    }

    private static string BuildArguments(double startSeconds, double lengthSeconds, string inputPath, int fps, int width, string outputPath)
    {
        var builder = new StringBuilder();
        builder.Append("-hide_banner -loglevel error -ss ");
        builder.Append(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(" -t ");
        builder.Append(lengthSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(" -i ");
        builder.Append('"').Append(inputPath.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        builder.Append(" -vf ");
        builder.Append('"').Append($"fps={fps},scale={width}:-1:flags=lanczos").Append('"');
        builder.Append(" -y ");
        builder.Append('"').Append(outputPath.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');

        return builder.ToString();
    }
}
