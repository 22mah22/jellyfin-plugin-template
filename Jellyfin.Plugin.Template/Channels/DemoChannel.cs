using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Template.Channels;

/// <summary>
/// Minimal proof-of-concept channel that exposes a single static item.
/// </summary>
internal sealed class DemoChannel : IChannel, IRequiresMediaInfoCallback
{
    private const string PocItemId = "poc-item-no-playback";
    private readonly ILogger<DemoChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DemoChannel"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DemoChannel(ILogger<DemoChannel> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Plugin Demo Channel";

    /// <inheritdoc />
    public string Description => "Proof-of-concept channel item surface. Playback intentionally not implemented.";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

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
            Name = "POC Item (No Playback)",
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Clip,
            Overview = "POC placeholder entry. Playback is intentionally not implemented.",
            RunTimeTicks = TimeSpan.FromSeconds(5).Ticks,
            DateModified = DateTimeOffset.UtcNow
        };

        return Task.FromResult(new ChannelItemResult
        {
            Items = [item],
            TotalRecordCount = 1
        });
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("DemoChannel.GetChannelItemMediaInfo invoked for item id {ItemId}.", id);

        var source = new MediaSourceInfo
        {
            Id = $"{PocItemId}-media-source",
            Name = "POC Placeholder Source",
            Path = "plugin-demo://not-implemented",
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = false
        };

        return Task.FromResult<IEnumerable<MediaSourceInfo>>([source]);
    }
}
