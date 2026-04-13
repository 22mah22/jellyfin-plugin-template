using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Data.Entities;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Template.Channels;

/// <summary>
/// Minimal proof-of-concept channel that exposes a single static item.
/// </summary>
internal sealed class DemoChannel : IChannel, IRequiresMediaInfoCallback
{
    private const string PocItemId = "poc-item-no-playback";
    private const string DemoItemPrefix = "demo-item:";
    private const int DefaultStartSeconds = 0;
    private const int DefaultLengthSeconds = 3;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DemoChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DemoChannel"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public DemoChannel(ILibraryManager libraryManager, ILogger<DemoChannel> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Plugin Demo Channel";

    /// <inheritdoc />
    public string Description => "Proof-of-concept channel item surface. Playback intentionally not implemented.";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public string DataVersion => "1";

    /// <inheritdoc />
    public string HomePageUrl => "https://example.invalid/plugin-demo-channel";

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            MediaTypes = [ChannelMediaType.Video],
            ContentTypes = [ChannelMediaContentType.Clip],
            SupportsContentDownloading = false
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
    {
        _logger.LogInformation("DemoChannel.IsEnabledFor invoked for user {UserId}.", userId);
        return true;
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        return Array.Empty<ImageType>();
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        _logger.LogInformation("DemoChannel.GetChannelImage invoked for image type {ImageType}.", type);
        return Task.FromResult(new DynamicImageResponse());
    }

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "DemoChannel.GetChannelItems invoked for user {UserId}, folder {FolderId}.",
            query.UserId,
            query.FolderId ?? "<root>");

        var item = new ChannelItemInfo
        {
            Id = PocItemId,
            Name = "POC Item (GIF Preview)",
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Clip,
            Overview = "POC item that attempts GIF generation from a selected real item id.",
            RunTimeTicks = TimeSpan.FromSeconds(5).Ticks,
            DateModified = DateTime.UtcNow
        };

        return Task.FromResult(new ChannelItemResult
        {
            Items = [item],
            TotalRecordCount = 1
        });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("DemoChannel.GetChannelItemMediaInfo invoked for item id {ItemId}.", id);

        var realItemId = GetRealItemId(id);
        if (!realItemId.HasValue)
        {
            _logger.LogWarning("Unable to parse real item id from channel id {ChannelItemId}.", id);
            return Array.Empty<MediaSourceInfo>();
        }

        var item = _libraryManager.GetItemById(realItemId.Value);
        if (item is null || item.MediaType != MediaType.Video || string.IsNullOrWhiteSpace(item.Path))
        {
            _logger.LogWarning("Real item id {ItemId} not found, not video, or missing local path.", realItemId.Value);
            return Array.Empty<MediaSourceInfo>();
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("Plugin instance was unavailable.");
            return Array.Empty<MediaSourceInfo>();
        }

        var generatedGifPath = await GenerateGifAsync(
                item.Path,
                realItemId.Value,
                plugin.ApplicationPaths.DataPath,
                plugin.Configuration.DefaultFps,
                plugin.Configuration.DefaultWidth,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(generatedGifPath))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var source = new MediaSourceInfo
        {
            Id = $"{realItemId.Value:N}-media-source",
            Name = "Generated GIF Preview",
            Path = generatedGifPath,
            IsRemote = false,
            Protocol = MediaProtocol.File,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Codec = "gif"
                }
            ]
        };

        return [source];
    }

    private static Guid? GetRealItemId(string channelItemId)
    {
        if (channelItemId.StartsWith(DemoItemPrefix, StringComparison.Ordinal))
        {
            var encodedId = channelItemId[DemoItemPrefix.Length..];
            if (Guid.TryParse(encodedId, out var parsedEncodedId))
            {
                return parsedEncodedId;
            }
        }

        if (Guid.TryParse(channelItemId, out var parsedId))
        {
            return parsedId;
        }

        return null;
    }

    private async Task<string?> GenerateGifAsync(
        string sourcePath,
        Guid realItemId,
        string dataPath,
        int fps,
        int width,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(dataPath, "plugins", "gif-generator", "generated");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, FormattableString.Invariant($"{realItemId:N}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_channel.gif"));
        var ffmpegPath = "ffmpeg";
        var filterGraph = $"fps={fps},scale={width}:-1:flags=lanczos";

        var processInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        processInfo.ArgumentList.Add("-y");
        processInfo.ArgumentList.Add("-ss");
        processInfo.ArgumentList.Add(DefaultStartSeconds.ToString(CultureInfo.InvariantCulture));
        processInfo.ArgumentList.Add("-t");
        processInfo.ArgumentList.Add(DefaultLengthSeconds.ToString(CultureInfo.InvariantCulture));
        processInfo.ArgumentList.Add("-i");
        processInfo.ArgumentList.Add(sourcePath);
        processInfo.ArgumentList.Add("-vf");
        processInfo.ArgumentList.Add(filterGraph);
        processInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = processInfo };
        try
        {
            process.Start();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Unable to start ffmpeg for channel item {ItemId}.", realItemId);
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg executable not available for channel item {ItemId}.", realItemId);
            return null;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _logger.LogWarning("ffmpeg channel generation failed for {ItemId} with exit code {ExitCode}: {Error}", realItemId, process.ExitCode, stderr);
            return null;
        }

        return outputPath;
    }
}
