using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Template.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        MaxGifLengthSeconds = 15;
        DefaultFps = 12;
        DefaultWidth = 480;
        GifRetentionHours = 168;
        SubtitleSeekMode = SubtitleSeekMode.Auto;
        SubtitleSeekPreRollSeconds = 2;
    }

    /// <summary>
    /// Gets or sets the maximum gif length, in seconds, accepted by the API.
    /// </summary>
    public int MaxGifLengthSeconds { get; set; }

    /// <summary>
    /// Gets or sets the default frames per second used during generation.
    /// </summary>
    public int DefaultFps { get; set; }

    /// <summary>
    /// Gets or sets the default gif width used during generation.
    /// </summary>
    public int DefaultWidth { get; set; }

    /// <summary>
    /// Gets or sets the number of hours generated gifs are retained before cleanup.
    /// </summary>
    public int GifRetentionHours { get; set; }

    /// <summary>
    /// Gets or sets subtitle seek strategy used when subtitle burn-in is enabled.
    /// </summary>
    public SubtitleSeekMode SubtitleSeekMode { get; set; }

    /// <summary>
    /// Gets or sets the pre-roll seconds used by hybrid subtitle seek mode.
    /// </summary>
    public double SubtitleSeekPreRollSeconds { get; set; }
}
