using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Template.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Minimum supported FPS for generated gifs.
    /// </summary>
    public const int MinGifFps = 1;

    /// <summary>
    /// Maximum supported FPS for generated gifs.
    /// </summary>
    public const int MaxGifFps = 60;

    /// <summary>
    /// Minimum supported width for generated gifs.
    /// </summary>
    public const int MinGifWidth = 16;

    /// <summary>
    /// Maximum supported width for generated gifs.
    /// </summary>
    public const int MaxGifWidth = 4096;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        MaxGifLengthSeconds = 15;
        DefaultFps = 12;
        DefaultWidth = 480;
        GifRetentionHours = 168;
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
    /// Clamps a configured default FPS to supported limits.
    /// </summary>
    /// <param name="fps">Configured FPS value.</param>
    /// <returns>A valid FPS value for ffmpeg arguments.</returns>
    public static int ClampDefaultFps(int fps)
        => Math.Clamp(fps, MinGifFps, MaxGifFps);

    /// <summary>
    /// Clamps a configured default width to supported limits.
    /// </summary>
    /// <param name="width">Configured width value.</param>
    /// <returns>A valid width value for ffmpeg arguments.</returns>
    public static int ClampDefaultWidth(int width)
        => Math.Clamp(width, MinGifWidth, MaxGifWidth);
}
