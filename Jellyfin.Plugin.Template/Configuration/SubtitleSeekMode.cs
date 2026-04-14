namespace Jellyfin.Plugin.Template.Configuration;

/// <summary>
/// Legacy subtitle seek mode enum retained for backward-compatible configuration deserialization.
/// </summary>
public enum SubtitleSeekMode
{
    /// <summary>
    /// Places seek after input for maximum subtitle timestamp alignment.
    /// </summary>
    Accurate = 0
}
