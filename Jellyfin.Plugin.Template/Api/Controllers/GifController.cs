using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Template.Api.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Template.Api.Controllers;

/// <summary>
/// API endpoints for authenticated gif generation.
/// Contract: all routes in this controller remain authorized-only; do not introduce anonymous endpoints.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/GifGenerator")]
public class GifController : ControllerBase
{
    private const double MaxSubtitleOffsetSeconds = 30;
    private const int MinimumGifRetentionHours = 1;
    private const int MaximumGifRetentionHours = 8760;
    private const int MaxGeneratedGifCount = 500;

    private static readonly Regex SafeGifFileNamePattern = new(@"^[A-Za-z0-9_.-]+\.gif$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex SignedSecondsWithUnitPattern = new(
        @"^(?<sign>[+-])?(?<value>\d+(?:\.\d+)?)(?<unit>ms|s)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ass",
        "cc_dec",
        "eia_608",
        "jacosub",
        "microdvd",
        "mov_text",
        "mpl2",
        "pjs",
        "realtext",
        "sami",
        "ssa",
        "srt",
        "subrip",
        "subviewer",
        "text",
        "ttml",
        "vplayer",
        "webvtt"
    };

    private static readonly HashSet<string> TextSubtitleFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ass",
        ".dfxp",
        ".sami",
        ".smi",
        ".srt",
        ".ssa",
        ".stl",
        ".ttml",
        ".txt",
        ".vtt"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<GifController> _logger;
    private readonly IApplicationPaths _serverApplicationPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="GifController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="serverApplicationPaths">The server application paths.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    public GifController(
        ILibraryManager libraryManager,
        ILogger<GifController> logger,
        IApplicationPaths serverApplicationPaths,
        IServerConfigurationManager serverConfigurationManager)
    {
        _libraryManager = libraryManager;
        _logger = logger;
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

        var subtitleOffsetResult = ParseSubtitleTimingOffsetSeconds(request.SubtitleTimingOffset);
        if (!subtitleOffsetResult.IsValid)
        {
            return BadRequest(subtitleOffsetResult.ErrorMessage);
        }

        if (!request.SubtitleStreamIndex.HasValue && Math.Abs(subtitleOffsetResult.Seconds) > 0)
        {
            return BadRequest("SubtitleTimingOffset requires SubtitleStreamIndex to be set.");
        }

        CleanupGeneratedGifs();

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null || item.MediaType != MediaType.Video || string.IsNullOrWhiteSpace(item.Path))
        {
            return NotFound("Video item was not found or does not have a local file path.");
        }

        var subtitleSelection = ResolveSubtitleSelection(item, request.SubtitleStreamIndex);
        if (!subtitleSelection.IsValid)
        {
            return BadRequest(subtitleSelection.ErrorMessage);
        }

        var ffmpegPath = ResolveFfmpegPath();

        var outputDirectory = Path.Combine(_serverApplicationPaths.DataPath, "plugins", "gif-generator", "generated");
        Directory.CreateDirectory(outputDirectory);

        var outputFileName = $"{request.ItemId:N}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.gif";
        var outputPath = Path.Combine(outputDirectory, outputFileName);

        var fps = request.Fps > 0 ? request.Fps : plugin.Configuration.DefaultFps;
        var width = request.Width > 0 ? request.Width : plugin.Configuration.DefaultWidth;

        var processInfo = BuildProcessStartInfo(
            ffmpegPath,
            request.StartSeconds,
            request.LengthSeconds,
            item.Path,
            fps,
            width,
            outputPath,
            subtitleSelection,
            request.SubtitleFontSize,
            subtitleOffsetResult.Seconds);

        using var process = new Process { StartInfo = processInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                $"Unable to start ffmpeg at '{ffmpegPath}'. Configure Jellyfin's encoder path to use Jellyfin's ffmpeg binary.");
        }

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
    /// Gets the available subtitle streams for a video item.
    /// </summary>
    /// <param name="itemId">The video item id.</param>
    /// <returns>The available subtitle streams for the item.</returns>
    [HttpGet("Subtitles/{itemId:guid}")]
    [ProducesResponseType<GetSubtitleStreamsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<GetSubtitleStreamsResponse> GetSubtitleStreams([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || item.MediaType != MediaType.Video)
        {
            return NotFound("Video item was not found.");
        }

        var subtitles = GetSubtitleStreams(item)
            .Select(stream => new SubtitleStreamOption
            {
                StreamIndex = stream.Index,
                Language = stream.Language,
                DisplayTitle = GetSubtitleDisplayTitle(stream),
                IsDefault = stream.IsDefault,
                IsForced = stream.IsForced,
                IsExternal = stream.IsExternal,
                IsTextBased = IsTextSubtitleStream(stream)
            })
            .ToList();

        return Ok(new GetSubtitleStreamsResponse
        {
            ItemId = itemId,
            Subtitles = subtitles
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
        CleanupGeneratedGifs();
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
        string outputPath,
        SubtitleSelection subtitleSelection,
        int? subtitleFontSize,
        double subtitleOffsetSeconds)
    {
        var start = startSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var length = lengthSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var videoFilter = BuildVideoFilter(fps, width, inputPath, subtitleSelection, subtitleFontSize, subtitleOffsetSeconds);

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
        var hasSubtitleBurnIn = subtitleSelection.FfmpegSubtitleOrdinal.HasValue || !string.IsNullOrEmpty(subtitleSelection.ExternalSubtitlePath);
        if (hasSubtitleBurnIn)
        {
            // Keep seek arguments after input so subtitle timestamps stay aligned for burn-in.
            processInfo.ArgumentList.Add("-i");
            processInfo.ArgumentList.Add(inputPath);
            processInfo.ArgumentList.Add("-ss");
            processInfo.ArgumentList.Add(start);
        }
        else
        {
            // Fast seek before input dramatically improves performance for large offsets.
            processInfo.ArgumentList.Add("-ss");
            processInfo.ArgumentList.Add(start);
            processInfo.ArgumentList.Add("-i");
            processInfo.ArgumentList.Add(inputPath);
        }

        processInfo.ArgumentList.Add("-t");
        processInfo.ArgumentList.Add(length);
        processInfo.ArgumentList.Add("-vf");
        processInfo.ArgumentList.Add(videoFilter);
        processInfo.ArgumentList.Add("-y");
        processInfo.ArgumentList.Add(outputPath);

        return processInfo;
    }

    private static string BuildVideoFilter(
        int fps,
        int width,
        string inputPath,
        SubtitleSelection subtitleSelection,
        int? subtitleFontSize,
        double subtitleOffsetSeconds)
    {
        var builder = new StringBuilder();
        var hasSubtitleBurnIn = subtitleSelection.FfmpegSubtitleOrdinal.HasValue || !string.IsNullOrEmpty(subtitleSelection.ExternalSubtitlePath);
        var hasSubtitleOffset = Math.Abs(subtitleOffsetSeconds) > 0;
        if (hasSubtitleOffset && hasSubtitleBurnIn)
        {
            // Shift only subtitle evaluation timing, then restore original timestamps so GIF frame selection remains anchored to StartSeconds.
            // Positive subtitle offset means subtitles appear later, so subtitle evaluation should see earlier timestamps.
            builder.Append("setpts=");
            builder.Append(BuildPtsOffsetExpression(subtitleOffsetSeconds, reverse: false));
            builder.Append(',');
        }

        if (hasSubtitleBurnIn)
        {
            builder.Append("subtitles=filename=");
            var subtitleInputPath = subtitleSelection.ExternalSubtitlePath ?? inputPath;
            builder.Append(EscapeFilterValue(subtitleInputPath));
            if (subtitleSelection.FfmpegSubtitleOrdinal.HasValue)
            {
                builder.Append(":si=");
                builder.Append(subtitleSelection.FfmpegSubtitleOrdinal.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (subtitleFontSize.HasValue)
            {
                builder.Append(":force_style=");
                builder.Append(EscapeFilterValue("Fontsize=" + subtitleFontSize.Value.ToString(CultureInfo.InvariantCulture)));
            }

            builder.Append(',');
        }

        if (hasSubtitleOffset && hasSubtitleBurnIn)
        {
            // Restore timestamps after subtitle burn-in so the GIF visual clip start remains unchanged.
            builder.Append("setpts=");
            builder.Append(BuildPtsOffsetExpression(subtitleOffsetSeconds, reverse: true));
            builder.Append(',');
        }

        builder.Append("fps=");
        builder.Append(fps.ToString(CultureInfo.InvariantCulture));
        builder.Append(",scale=");
        builder.Append(width.ToString(CultureInfo.InvariantCulture));
        builder.Append(":-1:flags=lanczos");
        return builder.ToString();
    }

    private static string BuildPtsOffsetExpression(double subtitleOffsetSeconds, bool reverse)
    {
        var applyNegativeOffset = subtitleOffsetSeconds > 0;
        if (reverse)
        {
            applyNegativeOffset = !applyNegativeOffset;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"PTS{(applyNegativeOffset ? "-" : "+")}{Math.Abs(subtitleOffsetSeconds).ToString("0.###", CultureInfo.InvariantCulture)}/TB");
    }

    private static string? GetSubtitleDisplayTitle(MediaStream stream)
    {
        var displayTitle = GetStreamStringPropertyValue(stream, "DisplayTitle");
        if (!string.IsNullOrWhiteSpace(displayTitle))
        {
            return displayTitle;
        }

        var title = GetStreamStringPropertyValue(stream, "Title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return null;
    }

    private static string? GetStreamStringPropertyValue(MediaStream stream, string propertyName)
    {
        var property = stream.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.PropertyType != typeof(string))
        {
            return null;
        }

        return property.GetValue(stream) as string;
    }

    private static IEnumerable<MediaStream> GetSubtitleStreams(BaseItem item)
        => item
            .GetMediaStreams()
            .Where(stream => stream.Type == MediaStreamType.Subtitle);

    private static SubtitleSelection ResolveSubtitleSelection(BaseItem item, int? subtitleStreamIndex)
    {
        if (!subtitleStreamIndex.HasValue)
        {
            return new SubtitleSelection(true, null, null, null);
        }

        var subtitleStreams = GetSubtitleStreams(item).ToList();
        var selectedSubtitle = subtitleStreams.FirstOrDefault(stream => stream.Index == subtitleStreamIndex.Value);
        if (selectedSubtitle is null)
        {
            return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' does not exist as a subtitle stream on the selected item.", null, null);
        }

        if (selectedSubtitle.IsExternal)
        {
            if (string.IsNullOrWhiteSpace(selectedSubtitle.Path))
            {
                return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' is external but does not expose a file path.", null, null);
            }

            var resolvedExternalPath = ResolveExternalSubtitlePath(item.Path, selectedSubtitle.Path);
            if (resolvedExternalPath is null)
            {
                return new SubtitleSelection(
                    false,
                    $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' points to missing subtitle file '{selectedSubtitle.Path}'. Re-scan metadata or pick another subtitle stream.",
                    null,
                    null);
            }

            if (!IsTextSubtitleStream(selectedSubtitle))
            {
                return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' uses a non-text subtitle format that ffmpeg cannot burn into GIFs. Choose a text subtitle stream (SRT/ASS/WebVTT) or generate without subtitles.", null, null);
            }

            return new SubtitleSelection(true, null, null, resolvedExternalPath);
        }

        if (!IsTextSubtitleStream(selectedSubtitle))
        {
            return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' uses codec '{selectedSubtitle.Codec ?? "unknown"}', which is image-based and not supported by ffmpeg's subtitles filter for GIF generation.", null, null);
        }

        var ffmpegSubtitleOrdinal = subtitleStreams
            .Where(stream => !stream.IsExternal)
            .Select((stream, index) => new { stream.Index, Ordinal = index })
            .FirstOrDefault(stream => stream.Index == subtitleStreamIndex.Value)?
            .Ordinal;

        if (!ffmpegSubtitleOrdinal.HasValue)
        {
            return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' could not be mapped to an ffmpeg subtitle stream ordinal.", null, null);
        }

        return new SubtitleSelection(true, null, ffmpegSubtitleOrdinal.Value, null);
    }

    private static string EscapeFilterValue(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal);
        return escaped;
    }

    [SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "subtitlePath/itemPath come from Jellyfin library metadata (MediaStream.Path/BaseItem.Path), not direct user input, and the resolved file is only used for ffmpeg subtitle burn-in.")]
    private static string? ResolveExternalSubtitlePath(string? itemPath, string subtitlePath)
    {
        if (string.IsNullOrWhiteSpace(subtitlePath))
        {
            return null;
        }

        if (System.IO.File.Exists(subtitlePath))
        {
            return subtitlePath;
        }

        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return null;
        }

        var itemDirectory = Path.GetDirectoryName(itemPath);
        if (string.IsNullOrWhiteSpace(itemDirectory) || !Directory.Exists(itemDirectory))
        {
            return null;
        }

        if (!Path.IsPathRooted(subtitlePath))
        {
            var relativeCandidate = Path.GetFullPath(Path.Combine(itemDirectory, subtitlePath));
            if (System.IO.File.Exists(relativeCandidate))
            {
                return relativeCandidate;
            }
        }

        var subtitleFileName = Path.GetFileName(subtitlePath);
        if (string.IsNullOrWhiteSpace(subtitleFileName))
        {
            return null;
        }

        var sameDirectoryCandidate = Path.Combine(itemDirectory, subtitleFileName);
        if (System.IO.File.Exists(sameDirectoryCandidate))
        {
            return sameDirectoryCandidate;
        }

        var extension = Path.GetExtension(subtitleFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var filesWithSameExtension = Directory.EnumerateFiles(itemDirectory, "*" + extension, SearchOption.TopDirectoryOnly);
        return filesWithSameExtension.FirstOrDefault(file => string.Equals(Path.GetFileName(file), subtitleFileName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTextSubtitleStream(MediaStream stream)
    {
        if (!string.IsNullOrWhiteSpace(stream.Codec))
        {
            return TextSubtitleCodecs.Contains(stream.Codec);
        }

        if (stream.IsExternal && !string.IsNullOrWhiteSpace(stream.Path))
        {
            return TextSubtitleFileExtensions.Contains(Path.GetExtension(stream.Path));
        }

        return false;
    }

    private string ResolveFfmpegPath()
    {
        var configuredPath = _serverConfigurationManager.GetEncodingOptions().EncoderAppPath;
        var resolvedConfiguredPath = ResolveConfiguredExecutable(configuredPath);
        if (!string.IsNullOrEmpty(resolvedConfiguredPath))
        {
            return resolvedConfiguredPath;
        }

        var jellyfinFfmpegPath = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG");
        var resolvedJellyfinFfmpegPath = ResolveConfiguredExecutable(jellyfinFfmpegPath);
        if (!string.IsNullOrEmpty(resolvedJellyfinFfmpegPath))
        {
            return resolvedJellyfinFfmpegPath;
        }

        var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        var resolvedFfmpegPath = ResolveConfiguredExecutable(ffmpegPath);
        if (!string.IsNullOrEmpty(resolvedFfmpegPath))
        {
            return resolvedFfmpegPath;
        }

        var pathResolvedFfmpeg = ResolveFromPathEnvironment();
        if (pathResolvedFfmpeg is not null)
        {
            return pathResolvedFfmpeg;
        }

        foreach (var candidate in GetKnownBundledFfmpegPaths())
        {
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
    }

    private static string? ResolveConfiguredExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmedPath = path.Trim();
        if (Path.IsPathRooted(trimmedPath))
        {
            return IsExecutableFile(trimmedPath) ? trimmedPath : null;
        }

        return trimmedPath;
    }

    private static bool IsExecutableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmedPath = path.Trim();
        if (!Path.IsPathRooted(trimmedPath) || !System.IO.File.Exists(trimmedPath))
        {
            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        try
        {
            var mode = System.IO.File.GetUnixFileMode(trimmedPath);
            return mode.HasFlag(UnixFileMode.UserExecute)
                || mode.HasFlag(UnixFileMode.GroupExecute)
                || mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? ResolveFromPathEnvironment()
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetKnownBundledFfmpegPaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            yield return Path.Combine(AppContext.BaseDirectory, "jellyfin-ffmpeg", "ffmpeg.exe");
            yield break;
        }

        yield return "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        yield return "/usr/local/lib/jellyfin-ffmpeg/ffmpeg";
        yield return "/app/jellyfin-ffmpeg/ffmpeg";
        yield return "/usr/bin/ffmpeg";
    }

    private static SubtitleOffsetParseResult ParseSubtitleTimingOffsetSeconds(string? subtitleTimingOffset)
    {
        if (string.IsNullOrWhiteSpace(subtitleTimingOffset))
        {
            return new SubtitleOffsetParseResult(true, null, 0);
        }

        var rawValue = subtitleTimingOffset.Trim();
        var match = SignedSecondsWithUnitPattern.Match(rawValue);
        if (match.Success)
        {
            var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            var isNegative = string.Equals(match.Groups["sign"].Value, "-", StringComparison.Ordinal);
            var unit = match.Groups["unit"].Value;

            var seconds = string.Equals(unit, "ms", StringComparison.OrdinalIgnoreCase)
                ? value / 1000d
                : value;
            if (isNegative)
            {
                seconds = -seconds;
            }

            return ValidateSubtitleOffsetBounds(seconds);
        }

        var hasExplicitSign = rawValue.Length > 0 && (rawValue[0] == '+' || rawValue[0] == '-');
        var normalized = hasExplicitSign
            ? rawValue
            : "+" + rawValue;

        var sign = normalized[0] == '-' ? -1d : 1d;
        var timeText = normalized[1..];
        var parts = timeText.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return new SubtitleOffsetParseResult(
                false,
                "SubtitleTimingOffset format is invalid. Use values like '+500ms', '-1.2s', '+00:01.250', or '-01:02:03.5'.",
                0);
        }

        var hoursText = parts.Length == 3 ? parts[0] : "0";
        var minutesText = parts[^2];
        var secondsText = parts[^1];
        if (!int.TryParse(hoursText, NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(secondsText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var secondsPart))
        {
            return new SubtitleOffsetParseResult(
                false,
                "SubtitleTimingOffset contains non-numeric time components.",
                0);
        }

        if (minutes >= 60 || secondsPart >= 60)
        {
            return new SubtitleOffsetParseResult(
                false,
                "SubtitleTimingOffset minutes and seconds must be less than 60 for timecode formats.",
                0);
        }

        var totalSeconds = sign * ((hours * 3600d) + (minutes * 60d) + secondsPart);
        return ValidateSubtitleOffsetBounds(totalSeconds);
    }

    private static SubtitleOffsetParseResult ValidateSubtitleOffsetBounds(double seconds)
    {
        if (Math.Abs(seconds) > MaxSubtitleOffsetSeconds)
        {
            return new SubtitleOffsetParseResult(
                false,
                $"SubtitleTimingOffset cannot exceed ±{MaxSubtitleOffsetSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.",
                0);
        }

        return new SubtitleOffsetParseResult(true, null, seconds);
    }

    private void CleanupGeneratedGifs()
    {
        var outputDirectory = Path.Combine(_serverApplicationPaths.DataPath, "plugins", "gif-generator", "generated");
        if (!Directory.Exists(outputDirectory))
        {
            return;
        }

        var plugin = Plugin.Instance;
        var configuredRetentionHours = plugin?.Configuration.GifRetentionHours ?? 168;
        var retentionHours = Math.Clamp(configuredRetentionHours, MinimumGifRetentionHours, MaximumGifRetentionHours);
        var expirationThresholdUtc = DateTimeOffset.UtcNow.AddHours(-retentionHours);

        var gifFiles = Directory
            .EnumerateFiles(outputDirectory, "*.gif", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();

        foreach (var gifFile in gifFiles)
        {
            if (gifFile.LastWriteTimeUtc >= expirationThresholdUtc)
            {
                continue;
            }

            TryDeleteFile(gifFile, $"retention window ({retentionHours}h)");
        }

        var remainingGifFiles = gifFiles
            .Where(file => file.Exists)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();

        if (remainingGifFiles.Count <= MaxGeneratedGifCount)
        {
            return;
        }

        var filesToPrune = remainingGifFiles.Take(remainingGifFiles.Count - MaxGeneratedGifCount);

        foreach (var gifFile in filesToPrune)
        {
            TryDeleteFile(gifFile, $"max generated gif count ({MaxGeneratedGifCount})");
        }
    }

    private void TryDeleteFile(FileInfo gifFile, string reason)
    {
        try
        {
            gifFile.Delete();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete generated gif file '{Path}' during cleanup for {Reason}.", gifFile.FullName, reason);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to delete generated gif file '{Path}' during cleanup for {Reason}.", gifFile.FullName, reason);
        }
    }

    private sealed record SubtitleOffsetParseResult(
        bool IsValid,
        string? ErrorMessage,
        double Seconds);

    private sealed record SubtitleSelection(
        bool IsValid,
        string? ErrorMessage,
        int? FfmpegSubtitleOrdinal,
        string? ExternalSubtitlePath);
}
