namespace Jellyfin.Plugin.Template.Api.Models;

/// <summary>
/// A subtitle stream option available for gif generation.
/// </summary>
public class SubtitleStreamOption
{
    /// <summary>
    /// Gets or sets the subtitle stream index.
    /// </summary>
    public int StreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the subtitle stream language code.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the display title for the subtitle stream.
    /// </summary>
    public string? DisplayTitle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this stream is marked default.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this stream is marked forced.
    /// </summary>
    public bool IsForced { get; set; }
}
