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
    /// Legacy value retained for backward-compatible configuration deserialization.
    /// </summary>
    [Obsolete("Deprecated. Values are normalized to Accurate.", error: false)]
    Fast = 1,

    /// <summary>
    /// Legacy value retained for backward-compatible configuration deserialization.
    /// </summary>
    [Obsolete("Deprecated. Values are normalized to Accurate.", error: false)]
    Hybrid = 2,

    /// <summary>
    /// Legacy value retained for backward-compatible configuration deserialization.
    /// </summary>
    [Obsolete("Deprecated. Values are normalized to Accurate.", error: false)]
    Auto = 3
}
