namespace Jellyfin.Plugin.Template.Configuration;

/// <summary>
/// Controls ffmpeg seek placement when subtitle burn-in is enabled.
/// </summary>
public enum SubtitleSeekMode
{
    /// <summary>
    /// Places seek after input for maximum subtitle timestamp alignment.
    /// </summary>
    Accurate = 0,

    /// <summary>
    /// Places seek before input for faster startup, with slight subtitle timing drift risk.
    /// </summary>
    Fast = 1,

    /// <summary>
    /// Uses a coarse seek before input and a fine seek after input as a balance.
    /// </summary>
    Hybrid = 2
}
