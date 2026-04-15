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
using Jellyfin.Plugin.Template.Configuration;
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
    private const double SystemSubtitleTimingCompensationSeconds = 0;
    private const int MinimumGifRetentionHours = 1;
    private const int MaximumGifRetentionHours = 8760;
    private const int MaxGeneratedGifCount = 500;

    private static readonly Regex SafeGifFileNamePattern = new(@"^[A-Za-z0-9_.-]+\.gif$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex SignedSecondsWithUnitPattern = new(
        @"^(?<sign>[+-])?(?<value>\d+(?:\.\d+)?)(?<unit>ms|s)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SrtTimingLinePattern = new(
        @"^\s*(?<start>\d{2}:\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<end>\d{2}:\d{2}:\d{2}[,.]\d{1,3})(?<settings>.*)$",
        RegexOptions.CultureInvariant,
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

    private static readonly string[] RecoverableTwoStepPreparationErrorMarkers =
    [
        "Subtitle encoding currently only possible",
        "Could not write header for output file",
        "Could not find tag for codec",
        "Error initializing output stream",
        "Error selecting an encoder",
        "Automatic encoder selection failed",
        "Encoder did not produce proper pts",
        "Subtitle codec 94213 is not supported",
        "Neither text nor ssav2 are accepted"
    ];

    private static readonly string[] UnrecoverableStageAFailureMarkers =
    [
        "No such file or directory",
        "Invalid data found when processing input",
        "Permission denied",
        "could not open input file",
        "Error opening input"
    ];

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

        var fps = request.Fps > 0 ? request.Fps : PluginConfiguration.ClampDefaultFps(plugin.Configuration.DefaultFps);
        if (fps < PluginConfiguration.MinGifFps || fps > PluginConfiguration.MaxGifFps)
        {
            return BadRequest($"Effective fps must be between {PluginConfiguration.MinGifFps} and {PluginConfiguration.MaxGifFps}. Provided effective value: {fps}.");
        }

        var width = request.Width > 0 ? request.Width : PluginConfiguration.ClampDefaultWidth(plugin.Configuration.DefaultWidth);
        if (width < PluginConfiguration.MinGifWidth || width > PluginConfiguration.MaxGifWidth)
        {
            return BadRequest($"Effective width must be between {PluginConfiguration.MinGifWidth} and {PluginConfiguration.MaxGifWidth}. Provided effective value: {width}.");
        }

        if ((width % 2) != 0)
        {
            // Normalize odd widths for better ffmpeg encoder/filter compatibility.
            width--;
        }

        var hasSubtitleBurnIn = subtitleSelection.FfmpegSubtitleOrdinal.HasValue || !string.IsNullOrEmpty(subtitleSelection.ExternalSubtitlePath);
        var requestStartedAtUtc = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "GIF generation started for item {ItemId}. start={StartSeconds}s length={LengthSeconds}s subtitleStreamIndex={SubtitleStreamIndex} selectedSubtitleJellyfinStreamIndex={SelectedSubtitleJellyfinStreamIndex} selectedSubtitleFfmpegOrdinal={SelectedSubtitleFfmpegOrdinal} requestedAtUtc={RequestedAtUtc:o}.",
            request.ItemId,
            request.StartSeconds,
            request.LengthSeconds,
            request.SubtitleStreamIndex,
            subtitleSelection.JellyfinSubtitleStreamIndex,
            subtitleSelection.FfmpegSubtitleOrdinal,
            requestStartedAtUtc);

        if (!hasSubtitleBurnIn)
        {
            // Keep the existing direct execution path for subtitle-free GIF generation.
            var directStageStopwatch = Stopwatch.StartNew();
            var processInfo = BuildDirectGifCmd(
                ffmpegPath,
                request.StartSeconds,
                request.LengthSeconds,
                item.Path,
                fps,
                width,
                outputPath);

            var directRunResult = await RunFfmpegAsync(processInfo, cancellationToken).ConfigureAwait(false);
            if (!directRunResult.IsSuccess)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, directRunResult.ErrorMessage);
            }

            directStageStopwatch.Stop();
            _logger.LogInformation(
                "GIF generation completed direct path for item {ItemId} in {DurationMs}ms.",
                request.ItemId,
                directStageStopwatch.ElapsedMilliseconds);
        }
        else
        {
            var subtitleTimingModel = BuildSubtitleTimingModel(request.StartSeconds, subtitleOffsetResult.Seconds);
            var twoStepResult = await RunSubtitleTwoStepPipelineAsync(
                request.ItemId,
                ffmpegPath,
                subtitleTimingModel,
                request.StartSeconds,
                request.LengthSeconds,
                item.Path,
                fps,
                width,
                outputPath,
                subtitleSelection,
                request.SubtitleFontSize,
                request.SubtitleStreamIndex,
                cancellationToken).ConfigureAwait(false);
            if (!twoStepResult.IsSuccess && twoStepResult.IsRecoverablePreparationFailure)
            {
                _logger.LogWarning(
                    "Recoverable subtitle two-step preparation failure for item {ItemId}; falling back to accurate single-pass pipeline. Reason: {ErrorMessage}",
                    request.ItemId,
                    twoStepResult.ErrorMessage);

                var singlePassResult = await RunSubtitleSinglePassPipelineAsync(
                    request.ItemId,
                    ffmpegPath,
                    subtitleTimingModel,
                    request.StartSeconds,
                    request.LengthSeconds,
                    item.Path,
                    fps,
                    width,
                    outputPath,
                    subtitleSelection,
                    request.SubtitleFontSize,
                    request.SubtitleStreamIndex,
                    cancellationToken).ConfigureAwait(false);

                if (!singlePassResult.IsSuccess)
                {
                    _logger.LogWarning(
                        "Subtitle GIF generation failed for both two-step and fallback single-pass paths for item {ItemId}. TwoStepError={TwoStepError} FallbackError={FallbackError}",
                        request.ItemId,
                        twoStepResult.ErrorMessage,
                        singlePassResult.ErrorMessage);

                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        $"Both subtitle pipeline paths failed. Fallback also failed after recoverable two-step preparation error. {singlePassResult.ErrorMessage}");
                }
            }
            else if (!twoStepResult.IsSuccess)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, twoStepResult.ErrorMessage);
            }
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

    private static ProcessStartInfo BuildDirectGifCmd(
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
        var videoFilter = BuildGifScaleAndFpsFilter(fps, width);

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
        // Fast seek before input dramatically improves performance for large offsets.
        processInfo.ArgumentList.Add("-ss");
        processInfo.ArgumentList.Add(start);
        processInfo.ArgumentList.Add("-i");
        processInfo.ArgumentList.Add(inputPath);

        processInfo.ArgumentList.Add("-t");
        processInfo.ArgumentList.Add(length);
        processInfo.ArgumentList.Add("-vf");
        processInfo.ArgumentList.Add(videoFilter);
        processInfo.ArgumentList.Add("-y");
        processInfo.ArgumentList.Add(outputPath);

        return processInfo;
    }

    private async Task<SubtitlePipelineRunResult> RunSubtitleTwoStepPipelineAsync(
        Guid itemId,
        string ffmpegPath,
        SubtitleTimingModel subtitleTimingModel,
        double requestStartSeconds,
        double lengthSeconds,
        string inputPath,
        int fps,
        int width,
        string outputPath,
        SubtitleSelection subtitleSelection,
        int? subtitleFontSize,
        int? requestedSubtitleStreamIndex,
        CancellationToken cancellationToken)
    {
        var pipelineStopwatch = Stopwatch.StartNew();
        var stageIdentifier = "stageA";
        string? clippedSubtitlePath = null;
        var tempDirectory = Path.Combine(
            _serverApplicationPaths.DataPath,
            "plugins",
            "gif-generator",
            "temp",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(tempDirectory);
        var intermediatePath = Path.Combine(tempDirectory, "stage-a.mkv");

        try
        {
            var stageAStopwatch = Stopwatch.StartNew();
            var stageAInfo = BuildStageACmd(
                ffmpegPath,
                subtitleTimingModel.SegmentStartSeconds,
                lengthSeconds,
                inputPath,
                intermediatePath,
                subtitleSelection.JellyfinSubtitleStreamIndex,
                subtitleSelection.FfmpegSubtitleOrdinal);
            var stageAResult = await RunFfmpegAsync(stageAInfo, cancellationToken).ConfigureAwait(false);
            stageAStopwatch.Stop();
            _logger.LogInformation(
                "GIF pipeline {StageId} completed for item {ItemId} in {DurationMs}ms (start={StartSeconds}s length={LengthSeconds}s subtitleStreamIndex={SubtitleStreamIndex} selectedSubtitleJellyfinStreamIndex={SelectedSubtitleJellyfinStreamIndex} selectedSubtitleFfmpegOrdinal={SelectedSubtitleFfmpegOrdinal}).",
                stageIdentifier,
                itemId,
                stageAStopwatch.ElapsedMilliseconds,
                requestStartSeconds,
                lengthSeconds,
                requestedSubtitleStreamIndex,
                subtitleSelection.JellyfinSubtitleStreamIndex,
                subtitleSelection.FfmpegSubtitleOrdinal);
            if (!stageAResult.IsSuccess)
            {
                var shouldAttemptFallback = ShouldAttemptSinglePassFallback(stageIdentifier, stageAResult.ErrorMessage);
                _logger.LogWarning(
                    "GIF pipeline {StageId} failed for item {ItemId}: {ErrorMessage}. fallbackSinglePass={FallbackSinglePass} selectedSubtitleJellyfinStreamIndex={SelectedSubtitleJellyfinStreamIndex} selectedSubtitleFfmpegOrdinal={SelectedSubtitleFfmpegOrdinal}",
                    stageIdentifier,
                    itemId,
                    stageAResult.ErrorMessage,
                    shouldAttemptFallback,
                    subtitleSelection.JellyfinSubtitleStreamIndex,
                    subtitleSelection.FfmpegSubtitleOrdinal);
                var twoStepErrorMessage = $"GIF pipeline failed at {stageIdentifier}: {stageAResult.ErrorMessage}";
                return new SubtitlePipelineRunResult(false, twoStepErrorMessage, shouldAttemptFallback);
            }

            stageIdentifier = "stageB";
            var stageBSubtitleSelection = RebaseSubtitleSelectionForIntermediateStage(subtitleSelection);
            var isExternalSubtitleFlow = !string.IsNullOrWhiteSpace(subtitleSelection.ExternalSubtitlePath) && !subtitleSelection.FfmpegSubtitleOrdinal.HasValue;
            if (isExternalSubtitleFlow)
            {
                var subtitleClipResult = await PrepareTemporaryExternalSubtitleClipAsync(
                    subtitleSelection.ExternalSubtitlePath!,
                    subtitleTimingModel.SegmentStartSeconds,
                    lengthSeconds,
                    tempDirectory,
                    cancellationToken).ConfigureAwait(false);
                if (!subtitleClipResult.IsSuccess)
                {
                    _logger.LogWarning(
                        "GIF pipeline stageB subtitle clipping failed for item {ItemId}. recoverable={Recoverable} sourceSubtitlePath={SourceSubtitlePath} windowStart={SegmentStartSeconds}s windowLength={LengthSeconds}s error={ErrorMessage}",
                        itemId,
                        subtitleClipResult.IsRecoverableFailure,
                        subtitleSelection.ExternalSubtitlePath,
                        subtitleTimingModel.SegmentStartSeconds,
                        lengthSeconds,
                        subtitleClipResult.ErrorMessage);
                    return new SubtitlePipelineRunResult(
                        false,
                        $"GIF pipeline failed at stageB subtitle clipping: {subtitleClipResult.ErrorMessage}",
                        subtitleClipResult.IsRecoverableFailure);
                }

                if (subtitleClipResult.IsTemporaryPreparedSubtitleFile)
                {
                    clippedSubtitlePath = subtitleClipResult.PreparedSubtitlePath;
                }

                stageBSubtitleSelection = stageBSubtitleSelection with { ExternalSubtitlePath = subtitleClipResult.PreparedSubtitlePath };
                _logger.LogInformation(
                    "GIF pipeline stageB using prepared external subtitle file for item {ItemId}. sourceSubtitlePath={SourceSubtitlePath} preparedSubtitlePath={PreparedSubtitlePath} keptCueCount={KeptCueCount} windowStart={SegmentStartSeconds}s windowLength={LengthSeconds}s.",
                    itemId,
                    subtitleSelection.ExternalSubtitlePath,
                    subtitleClipResult.PreparedSubtitlePath,
                    subtitleClipResult.KeptCueCount,
                    subtitleTimingModel.SegmentStartSeconds,
                    lengthSeconds);
            }

            var stageBStopwatch = Stopwatch.StartNew();
            var stageBInfo = BuildStageBCmd(
                ffmpegPath,
                intermediatePath,
                fps,
                width,
                outputPath,
                stageBSubtitleSelection,
                subtitleFontSize,
                subtitleTimingModel.EffectiveSubtitleOffsetSeconds);
            var stageBResult = await RunFfmpegAsync(stageBInfo, cancellationToken).ConfigureAwait(false);
            stageBStopwatch.Stop();
            _logger.LogInformation(
                "GIF pipeline {StageId} completed for item {ItemId} in {DurationMs}ms (start={StartSeconds}s length={LengthSeconds}s subtitleStreamIndex={SubtitleStreamIndex} selectedSubtitleJellyfinStreamIndex={SelectedSubtitleJellyfinStreamIndex} selectedSubtitleFfmpegOrdinal={SelectedSubtitleFfmpegOrdinal}).",
                stageIdentifier,
                itemId,
                stageBStopwatch.ElapsedMilliseconds,
                requestStartSeconds,
                lengthSeconds,
                requestedSubtitleStreamIndex,
                stageBSubtitleSelection.JellyfinSubtitleStreamIndex,
                stageBSubtitleSelection.FfmpegSubtitleOrdinal);
            if (!stageBResult.IsSuccess)
            {
                _logger.LogWarning(
                    "GIF pipeline {StageId} failed for item {ItemId}: {ErrorMessage}. selectedSubtitleJellyfinStreamIndex={SelectedSubtitleJellyfinStreamIndex} selectedSubtitleFfmpegOrdinal={SelectedSubtitleFfmpegOrdinal}",
                    stageIdentifier,
                    itemId,
                    stageBResult.ErrorMessage,
                    stageBSubtitleSelection.JellyfinSubtitleStreamIndex,
                    stageBSubtitleSelection.FfmpegSubtitleOrdinal);
                return new SubtitlePipelineRunResult(false, $"GIF pipeline failed at {stageIdentifier}: {stageBResult.ErrorMessage}", false);
            }

            pipelineStopwatch.Stop();
            _logger.LogInformation(
                "GIF subtitle pipeline completed for item {ItemId} in {DurationMs}ms.",
                itemId,
                pipelineStopwatch.ElapsedMilliseconds);
            return new SubtitlePipelineRunResult(true, null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "GIF pipeline canceled at {StageId} for item {ItemId}. start={StartSeconds}s length={LengthSeconds}s subtitleStreamIndex={SubtitleStreamIndex}.",
                stageIdentifier,
                itemId,
                requestStartSeconds,
                lengthSeconds,
                requestedSubtitleStreamIndex);
            return new SubtitlePipelineRunResult(false, $"GIF pipeline was canceled at {stageIdentifier}.", false);
        }
        finally
        {
            TryDeleteFile(intermediatePath);
            if (!string.IsNullOrWhiteSpace(clippedSubtitlePath))
            {
                var normalizedTempDirectory = Path.GetFullPath(tempDirectory);
                var normalizedClippedPath = Path.GetFullPath(clippedSubtitlePath);
                if (normalizedClippedPath.StartsWith(normalizedTempDirectory, StringComparison.Ordinal))
                {
                    TryDeleteFile(clippedSubtitlePath);
                }
            }

            if (Directory.Exists(tempDirectory))
            {
                foreach (var artifactPath in Directory.EnumerateFiles(tempDirectory, "subtitle-*", SearchOption.TopDirectoryOnly))
                {
                    TryDeleteFile(artifactPath);
                }
            }

            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task<FfmpegRunResult> RunSubtitleSinglePassPipelineAsync(
        Guid itemId,
        string ffmpegPath,
        SubtitleTimingModel subtitleTimingModel,
        double requestStartSeconds,
        double lengthSeconds,
        string inputPath,
        int fps,
        int width,
        string outputPath,
        SubtitleSelection subtitleSelection,
        int? subtitleFontSize,
        int? requestedSubtitleStreamIndex,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var processInfo = BuildSubtitleSinglePassCmd(
            ffmpegPath,
            subtitleTimingModel.SegmentStartSeconds,
            lengthSeconds,
            inputPath,
            fps,
            width,
            outputPath,
            subtitleSelection,
            subtitleFontSize,
            subtitleTimingModel.EffectiveSubtitleOffsetSeconds);

        var runResult = await RunFfmpegAsync(processInfo, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (!runResult.IsSuccess)
        {
            _logger.LogWarning(
                "GIF subtitle fallback single-pass pipeline failed for item {ItemId} after {DurationMs}ms (start={StartSeconds}s length={LengthSeconds}s subtitleStreamIndex={SubtitleStreamIndex} selectedSubtitleJellyfinStreamIndex={SelectedSubtitleJellyfinStreamIndex} selectedSubtitleFfmpegOrdinal={SelectedSubtitleFfmpegOrdinal}). Error: {ErrorMessage}",
                itemId,
                stopwatch.ElapsedMilliseconds,
                requestStartSeconds,
                lengthSeconds,
                requestedSubtitleStreamIndex,
                subtitleSelection.JellyfinSubtitleStreamIndex,
                subtitleSelection.FfmpegSubtitleOrdinal,
                runResult.ErrorMessage);
            return new FfmpegRunResult(false, $"GIF fallback single-pass pipeline failed: {runResult.ErrorMessage}");
        }

        _logger.LogInformation(
            "GIF subtitle fallback single-pass pipeline completed for item {ItemId} in {DurationMs}ms (start={StartSeconds}s length={LengthSeconds}s subtitleStreamIndex={SubtitleStreamIndex} selectedSubtitleJellyfinStreamIndex={SelectedSubtitleJellyfinStreamIndex} selectedSubtitleFfmpegOrdinal={SelectedSubtitleFfmpegOrdinal}).",
            itemId,
            stopwatch.ElapsedMilliseconds,
            requestStartSeconds,
            lengthSeconds,
            requestedSubtitleStreamIndex,
            subtitleSelection.JellyfinSubtitleStreamIndex,
            subtitleSelection.FfmpegSubtitleOrdinal);

        return new FfmpegRunResult(true, null);
    }

    private static bool ShouldAttemptSinglePassFallback(string stageIdentifier, string? errorMessage)
    {
        if (!string.Equals(stageIdentifier, "stageA", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsClearlyUnrecoverableStageAFailure(errorMessage))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return true;
        }

        if (IsRecoverableTwoStepPreparationFailure(errorMessage))
        {
            return true;
        }

        return true;
    }

    private static bool IsRecoverableTwoStepPreparationFailure(string errorMessage)
        => RecoverableTwoStepPreparationErrorMarkers.Any(marker => errorMessage.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsClearlyUnrecoverableStageAFailure(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return UnrecoverableStageAFailureMarkers.Any(marker => errorMessage.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static ProcessStartInfo BuildStageACmd(
        string ffmpegPath,
        double startSeconds,
        double lengthSeconds,
        string inputPath,
        string intermediatePath,
        int? jellyfinSubtitleStreamIndex,
        int? ffmpegSubtitleOrdinal)
    {
        var start = startSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var length = lengthSeconds.ToString("0.###", CultureInfo.InvariantCulture);

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
        processInfo.ArgumentList.Add("-i");
        processInfo.ArgumentList.Add(inputPath);
        // Accurate seek on subtitle path keeps subtitle timing aligned for burn-in.
        processInfo.ArgumentList.Add("-ss");
        processInfo.ArgumentList.Add(start);
        processInfo.ArgumentList.Add("-t");
        processInfo.ArgumentList.Add(length);
        processInfo.ArgumentList.Add("-map");
        processInfo.ArgumentList.Add("0:v:0");
        if (jellyfinSubtitleStreamIndex.HasValue && ffmpegSubtitleOrdinal.HasValue)
        {
            processInfo.ArgumentList.Add("-map");
            processInfo.ArgumentList.Add($"0:s:{ffmpegSubtitleOrdinal.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        processInfo.ArgumentList.Add("-an");
        processInfo.ArgumentList.Add("-c:v");
        processInfo.ArgumentList.Add("libx264");
        processInfo.ArgumentList.Add("-preset");
        processInfo.ArgumentList.Add("veryfast");
        processInfo.ArgumentList.Add("-crf");
        processInfo.ArgumentList.Add("18");
        if (jellyfinSubtitleStreamIndex.HasValue && ffmpegSubtitleOrdinal.HasValue)
        {
            processInfo.ArgumentList.Add("-c:s");
            processInfo.ArgumentList.Add("copy");
        }

        processInfo.ArgumentList.Add("-y");
        processInfo.ArgumentList.Add(intermediatePath);

        return processInfo;
    }

    private static ProcessStartInfo BuildSubtitleSinglePassCmd(
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
        processInfo.ArgumentList.Add("-i");
        processInfo.ArgumentList.Add(inputPath);
        processInfo.ArgumentList.Add("-ss");
        processInfo.ArgumentList.Add(start);
        processInfo.ArgumentList.Add("-t");
        processInfo.ArgumentList.Add(length);
        processInfo.ArgumentList.Add("-vf");
        processInfo.ArgumentList.Add(BuildVideoFilter(fps, width, inputPath, subtitleSelection, subtitleFontSize, subtitleOffsetSeconds));
        processInfo.ArgumentList.Add("-y");
        processInfo.ArgumentList.Add(outputPath);

        return processInfo;
    }

    private static ProcessStartInfo BuildStageBCmd(
        string ffmpegPath,
        string intermediatePath,
        int fps,
        int width,
        string outputPath,
        SubtitleSelection subtitleSelection,
        int? subtitleFontSize,
        double subtitleOffsetSeconds)
    {
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
        processInfo.ArgumentList.Add("-i");
        processInfo.ArgumentList.Add(intermediatePath);
        processInfo.ArgumentList.Add("-vf");
        processInfo.ArgumentList.Add(BuildVideoFilter(fps, width, intermediatePath, subtitleSelection, subtitleFontSize, subtitleOffsetSeconds));
        processInfo.ArgumentList.Add("-y");
        processInfo.ArgumentList.Add(outputPath);

        return processInfo;
    }

    private static string BuildGifScaleAndFpsFilter(int fps, int width)
        => string.Create(CultureInfo.InvariantCulture, $"fps={fps},scale={width}:-1:flags=lanczos");

    private static SubtitleSelection RebaseSubtitleSelectionForIntermediateStage(SubtitleSelection subtitleSelection)
    {
        if (!subtitleSelection.FfmpegSubtitleOrdinal.HasValue || subtitleSelection.FfmpegSubtitleOrdinal.Value == 0)
        {
            return subtitleSelection;
        }

        return subtitleSelection with { FfmpegSubtitleOrdinal = 0 };
    }

    private static string BuildVideoFilter(
        int fps,
        int width,
        string inputPath,
        SubtitleSelection subtitleSelection,
        int? subtitleFontSize,
        double effectiveSubtitleOffsetSeconds)
    {
        var builder = new StringBuilder();
        var hasSubtitleBurnIn = subtitleSelection.FfmpegSubtitleOrdinal.HasValue || !string.IsNullOrEmpty(subtitleSelection.ExternalSubtitlePath);
        var hasSubtitleOffset = Math.Abs(effectiveSubtitleOffsetSeconds) > 0;
        if (hasSubtitleOffset && hasSubtitleBurnIn)
        {
            // Shift only subtitle evaluation timing, then restore original timestamps so GIF frame selection remains anchored to StartSeconds.
            // Positive subtitle offset means subtitles appear later, so subtitle evaluation should see earlier timestamps.
            builder.Append("setpts=");
            builder.Append(BuildPtsOffsetExpression(effectiveSubtitleOffsetSeconds, reverse: false));
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
            builder.Append(BuildPtsOffsetExpression(effectiveSubtitleOffsetSeconds, reverse: true));
            builder.Append(',');
        }

        builder.Append("fps=");
        builder.Append(fps.ToString(CultureInfo.InvariantCulture));
        builder.Append(",scale=");
        builder.Append(width.ToString(CultureInfo.InvariantCulture));
        builder.Append(":-1:flags=lanczos");
        return builder.ToString();
    }

    private static SubtitleTimingModel BuildSubtitleTimingModel(double startSeconds, double userSubtitleOffsetSeconds)
    {
        var segmentStartSeconds = startSeconds;
        var relativeClipStartSeconds = startSeconds - segmentStartSeconds;
        var effectiveSubtitleOffsetSeconds = SystemSubtitleTimingCompensationSeconds + userSubtitleOffsetSeconds;
        return new SubtitleTimingModel(segmentStartSeconds, relativeClipStartSeconds, effectiveSubtitleOffsetSeconds);
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
            return new SubtitleSelection(true, null, null, null, null);
        }

        var subtitleStreams = GetSubtitleStreams(item).ToList();
        var selectedSubtitle = subtitleStreams.FirstOrDefault(stream => stream.Index == subtitleStreamIndex.Value);
        if (selectedSubtitle is null)
        {
            return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' does not exist as a subtitle stream on the selected item.", null, null, null);
        }

        if (selectedSubtitle.IsExternal)
        {
            if (string.IsNullOrWhiteSpace(selectedSubtitle.Path))
            {
                return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' is external but does not expose a file path.", null, null, null);
            }

            var resolvedExternalPath = ResolveExternalSubtitlePath(item.Path, selectedSubtitle.Path);
            if (resolvedExternalPath is null)
            {
                return new SubtitleSelection(
                    false,
                    $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' points to missing subtitle file '{selectedSubtitle.Path}'. Re-scan metadata or pick another subtitle stream.",
                    null,
                    null,
                    null);
            }

            if (!IsTextSubtitleStream(selectedSubtitle))
            {
                return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' uses a non-text subtitle format that ffmpeg cannot burn into GIFs. Choose a text subtitle stream (SRT/ASS/WebVTT) or generate without subtitles.", null, null, null);
            }

            return new SubtitleSelection(true, null, selectedSubtitle.Index, null, resolvedExternalPath);
        }

        if (!IsTextSubtitleStream(selectedSubtitle))
        {
            return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' uses codec '{selectedSubtitle.Codec ?? "unknown"}', which is image-based and not supported by ffmpeg's subtitles filter for GIF generation.", null, null, null);
        }

        var ffmpegSubtitleOrdinal = subtitleStreams
            .Where(stream => !stream.IsExternal)
            .Select((stream, index) => new { stream.Index, Ordinal = index })
            .FirstOrDefault(stream => stream.Index == subtitleStreamIndex.Value)?
            .Ordinal;

        if (!ffmpegSubtitleOrdinal.HasValue)
        {
            return new SubtitleSelection(false, $"SubtitleStreamIndex '{subtitleStreamIndex.Value}' could not be mapped to an ffmpeg subtitle stream ordinal.", selectedSubtitle.Index, null, null);
        }

        return new SubtitleSelection(true, null, selectedSubtitle.Index, ffmpegSubtitleOrdinal.Value, null);
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

    private async Task<PreparedSubtitleClipResult> PrepareTemporaryExternalSubtitleClipAsync(
        string externalSubtitlePath,
        double segmentStartSeconds,
        double lengthSeconds,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(externalSubtitlePath);
        if (string.Equals(extension, ".srt", StringComparison.OrdinalIgnoreCase))
        {
            return await PrepareSrtSubtitleClipAsync(
                externalSubtitlePath,
                segmentStartSeconds,
                lengthSeconds,
                tempDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(extension, ".ass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".ssa", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "ASS/SSA subtitle clipping is not yet implemented; using original subtitle file for Stage B. sourceSubtitlePath={SourceSubtitlePath} windowStart={SegmentStartSeconds}s windowLength={LengthSeconds}s.",
                externalSubtitlePath,
                segmentStartSeconds,
                lengthSeconds);
            return new PreparedSubtitleClipResult(true, false, null, externalSubtitlePath, null, false);
        }

        _logger.LogWarning(
            "Unsupported external subtitle extension '{Extension}' for clipping; using original subtitle file for Stage B. sourceSubtitlePath={SourceSubtitlePath}.",
            extension,
            externalSubtitlePath);
        return new PreparedSubtitleClipResult(true, false, null, externalSubtitlePath, null, false);
    }

    [SuppressMessage(
        "Security",
        "CA3003:Review code for file path injection vulnerabilities",
        Justification = "externalSubtitlePath is resolved from Jellyfin MediaStream metadata via ResolveSubtitleSelection/ResolveExternalSubtitlePath and not accepted directly from request input.")]
    private async Task<PreparedSubtitleClipResult> PrepareSrtSubtitleClipAsync(
        string externalSubtitlePath,
        double segmentStartSeconds,
        double lengthSeconds,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!System.IO.File.Exists(externalSubtitlePath))
            {
                return new PreparedSubtitleClipResult(
                    false,
                    false,
                    $"External subtitle file was not found: '{externalSubtitlePath}'.",
                    null,
                    null,
                    false);
            }

            var outputPath = Path.Combine(tempDirectory, "subtitle-clipped.srt");
            var clippingResult = await ClipSrtSubtitleWindowAsync(
                externalSubtitlePath,
                outputPath,
                segmentStartSeconds,
                lengthSeconds,
                cancellationToken).ConfigureAwait(false);
            return new PreparedSubtitleClipResult(true, false, null, outputPath, clippingResult.KeptCueCount, true);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new PreparedSubtitleClipResult(false, false, $"Unable to access external subtitle path '{externalSubtitlePath}': {ex.Message}", null, null, false);
        }
        catch (DirectoryNotFoundException ex)
        {
            return new PreparedSubtitleClipResult(false, false, $"Subtitle clipping directory path was not found while processing '{externalSubtitlePath}': {ex.Message}", null, null, false);
        }
        catch (PathTooLongException ex)
        {
            return new PreparedSubtitleClipResult(false, false, $"Subtitle clipping path is too long for '{externalSubtitlePath}': {ex.Message}", null, null, false);
        }
        catch (IOException ex)
        {
            return new PreparedSubtitleClipResult(false, false, $"I/O failure while clipping subtitle '{externalSubtitlePath}': {ex.Message}", null, null, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Recoverable subtitle clipping failure for source subtitle file '{SourceSubtitlePath}'.", externalSubtitlePath);
            return new PreparedSubtitleClipResult(
                false,
                true,
                $"Unable to clip external subtitle file '{externalSubtitlePath}' for two-step burn-in. {ex.Message}",
                null,
                null,
                false);
        }
    }

    private static async Task<SrtClipResult> ClipSrtSubtitleWindowAsync(
        string sourceSubtitlePath,
        string outputSubtitlePath,
        double segmentStartSeconds,
        double lengthSeconds,
        CancellationToken cancellationToken)
    {
        var segmentStart = TimeSpan.FromSeconds(segmentStartSeconds);
        var segmentEnd = segmentStart + TimeSpan.FromSeconds(lengthSeconds);
        var input = await System.IO.File.ReadAllTextAsync(sourceSubtitlePath, cancellationToken).ConfigureAwait(false);
        var normalizedInput = input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var rawBlocks = normalizedInput
            .Split("\n\n", StringSplitOptions.None)
            .Where(block => !string.IsNullOrWhiteSpace(block));

        var outputBuilder = new StringBuilder();
        var cueIndex = 1;
        foreach (var rawBlock in rawBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockLines = rawBlock.Split('\n', StringSplitOptions.None);
            var timingLineIndex = Array.FindIndex(blockLines, line => SrtTimingLinePattern.IsMatch(line));
            if (timingLineIndex < 0)
            {
                continue;
            }

            var timingMatch = SrtTimingLinePattern.Match(blockLines[timingLineIndex]);
            var sourceStart = ParseSrtTimestamp(timingMatch.Groups["start"].Value);
            var sourceEnd = ParseSrtTimestamp(timingMatch.Groups["end"].Value);
            if (sourceEnd <= sourceStart)
            {
                continue;
            }

            if (sourceEnd <= segmentStart || sourceStart >= segmentEnd)
            {
                continue;
            }

            var clippedStart = sourceStart - segmentStart;
            var clippedEnd = sourceEnd - segmentStart;
            if (clippedStart < TimeSpan.Zero)
            {
                clippedStart = TimeSpan.Zero;
            }

            if (clippedEnd <= TimeSpan.Zero)
            {
                continue;
            }

            if (clippedEnd > TimeSpan.FromSeconds(lengthSeconds))
            {
                clippedEnd = TimeSpan.FromSeconds(lengthSeconds);
            }

            if (clippedEnd <= clippedStart)
            {
                clippedEnd = clippedStart + TimeSpan.FromMilliseconds(1);
            }

            outputBuilder.Append(cueIndex.ToString(CultureInfo.InvariantCulture));
            outputBuilder.AppendLine();
            outputBuilder.Append(FormatSrtTimestamp(clippedStart));
            outputBuilder.Append(" --> ");
            outputBuilder.Append(FormatSrtTimestamp(clippedEnd));
            outputBuilder.Append(timingMatch.Groups["settings"].Value);
            outputBuilder.AppendLine();
            for (var i = timingLineIndex + 1; i < blockLines.Length; i++)
            {
                outputBuilder.AppendLine(blockLines[i]);
            }

            outputBuilder.AppendLine();
            cueIndex++;
        }

        await System.IO.File.WriteAllTextAsync(outputSubtitlePath, outputBuilder.ToString(), cancellationToken).ConfigureAwait(false);
        return new SrtClipResult(cueIndex - 1);
    }

    private static TimeSpan ParseSrtTimestamp(string value)
    {
        var normalized = value.Replace(',', '.');
        var parts = normalized.Split(':');
        if (parts.Length != 3)
        {
            throw new FormatException($"Invalid SRT timestamp '{value}'.");
        }

        var secondsParts = parts[2].Split('.', StringSplitOptions.None);
        if (secondsParts.Length is < 1 or > 2)
        {
            throw new FormatException($"Invalid SRT timestamp seconds field '{value}'.");
        }

        var hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var seconds = int.Parse(secondsParts[0], CultureInfo.InvariantCulture);
        var milliseconds = 0;
        if (secondsParts.Length == 2)
        {
            var fractionalSeconds = secondsParts[1].PadRight(3, '0');
            milliseconds = int.Parse(fractionalSeconds[..3], CultureInfo.InvariantCulture);
        }

        return new TimeSpan(0, hours, minutes, seconds, milliseconds);
    }

    private static string FormatSrtTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var totalHours = (int)value.TotalHours;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}");
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

    private static async Task<FfmpegRunResult> RunFfmpegAsync(ProcessStartInfo processInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = processInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return new FfmpegRunResult(
                false,
                $"Unable to start ffmpeg at '{processInfo.FileName}'. Configure Jellyfin's encoder path to use Jellyfin's ffmpeg binary.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryTerminateProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return new FfmpegRunResult(false, "ffmpeg execution was canceled.");
        }

        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            return new FfmpegRunResult(false, $"ffmpeg failed with exit code {process.ExitCode}. {stderr}");
        }

        return new FfmpegRunResult(true, null);
    }

    private static void TryTerminateProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        catch (NotSupportedException)
        {
            process.Kill();
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to delete temporary gif pipeline file '{Path}'.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Failed to delete temporary gif pipeline file '{Path}'.", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to delete temporary gif pipeline directory '{Path}'.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Failed to delete temporary gif pipeline directory '{Path}'.", path);
        }
    }

    private readonly record struct SubtitleTimingModel(
        double SegmentStartSeconds,
        double RelativeClipStartSeconds,
        double EffectiveSubtitleOffsetSeconds);

    private sealed record FfmpegRunResult(
        bool IsSuccess,
        string? ErrorMessage);

    private sealed record SubtitlePipelineRunResult(
        bool IsSuccess,
        string? ErrorMessage,
        bool IsRecoverablePreparationFailure);

    private sealed record SubtitleOffsetParseResult(
        bool IsValid,
        string? ErrorMessage,
        double Seconds);

    private sealed record SubtitleSelection(
        bool IsValid,
        string? ErrorMessage,
        int? JellyfinSubtitleStreamIndex,
        int? FfmpegSubtitleOrdinal,
        string? ExternalSubtitlePath);

    private sealed record PreparedSubtitleClipResult(
        bool IsSuccess,
        bool IsRecoverableFailure,
        string? ErrorMessage,
        string? PreparedSubtitlePath,
        int? KeptCueCount,
        bool IsTemporaryPreparedSubtitleFile);

    private sealed record SrtClipResult(int KeptCueCount);
}
