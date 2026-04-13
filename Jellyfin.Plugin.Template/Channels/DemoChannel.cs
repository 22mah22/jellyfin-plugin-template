using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Template.Channels;

/// <summary>
/// Minimal proof-of-concept channel that offers a GIF-focused browsing flow.
/// </summary>
internal sealed class DemoChannel : IChannel, IRequiresMediaInfoCallback
{
    private const string LibraryFolderId = "gif-library-browser";
    private const string InstructionsItemId = "gif-generator-instructions";
    private const string FallbackItemId = "poc-item-no-playback";
    private const int MaxLibraryResults = 50;
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
    public string Description => "Interactive demo surface for launching GIF creation from library items.";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public string DataVersion => "2";

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

        if (string.Equals(query.FolderId, LibraryFolderId, StringComparison.Ordinal))
        {
            return Task.FromResult(CreateLibraryItemResult());
        }

        return Task.FromResult(CreateRootItemResult());
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("DemoChannel.GetChannelItemMediaInfo invoked for item id {ItemId}.", id);

        var source = new MediaSourceInfo
        {
            Id = $"{id}-media-source",
            Name = "POC Placeholder Source",
            Path = "plugin-demo://not-implemented",
            IsRemote = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = false
        };

        return Task.FromResult<IEnumerable<MediaSourceInfo>>([source]);
    }

    private ChannelItemResult CreateRootItemResult()
    {
        var items = new List<ChannelItemInfo>
        {
            new()
            {
                Id = LibraryFolderId,
                Name = "Create GIF From Library Items",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Browse up to 50 local videos. Open an item and use the Create GIF action.",
                DateModified = DateTime.UtcNow
            },
            new()
            {
                Id = InstructionsItemId,
                Name = "How To Create A GIF",
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                Overview = "1) Open the folder item. 2) Pick a video. 3) Click Create GIF in the item actions.",
                RunTimeTicks = TimeSpan.FromSeconds(5).Ticks,
                DateModified = DateTime.UtcNow
            }
        };

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private ChannelItemResult CreateLibraryItemResult()
    {
        var libraryItems = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                HasPath = true,
                IsVirtualItem = false,
                MediaTypes = ["Video"],
                Limit = MaxLibraryResults
            })
            .ToList();

        if (libraryItems.Count == 0)
        {
            _logger.LogInformation("DemoChannel.GetChannelItems found no library videos. Returning fallback POC item.");

            var fallbackItem = new ChannelItemInfo
            {
                Id = FallbackItemId,
                Name = "POC Item (No Playback)",
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                Overview = "No local videos were found. Add media to your library, then reopen this channel to create GIFs.",
                RunTimeTicks = TimeSpan.FromSeconds(5).Ticks,
                DateModified = DateTime.UtcNow
            };

            return new ChannelItemResult
            {
                Items = [fallbackItem],
                TotalRecordCount = 1
            };
        }

        var items = libraryItems
            .Select(item => new ChannelItemInfo
            {
                Id = item.Id.ToString("N"),
                Name = item.Name,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                Overview = item.Overview,
                RunTimeTicks = item.RunTimeTicks,
                DateModified = item.DateModified
            })
            .ToList();

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }
}
