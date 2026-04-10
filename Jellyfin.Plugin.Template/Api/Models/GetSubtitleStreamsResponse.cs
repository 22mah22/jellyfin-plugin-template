namespace Jellyfin.Plugin.Template.Api.Models;

/// <summary>
/// Response payload containing subtitle stream options for a media item.
/// </summary>
public class GetSubtitleStreamsResponse
{
    /// <summary>
    /// Gets or sets the item id used to resolve subtitle streams.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the available internal subtitle streams.
    /// </summary>
    public IReadOnlyList<SubtitleStreamOption> Subtitles { get; set; } = [];
}
