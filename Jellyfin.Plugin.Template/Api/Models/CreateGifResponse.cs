namespace Jellyfin.Plugin.Template.Api.Models;

/// <summary>
/// Response payload returned after gif generation.
/// </summary>
public class CreateGifResponse
{
    /// <summary>
    /// Gets or sets the generated file name.
    /// </summary>
    public required string FileName { get; set; }

    /// <summary>
    /// Gets or sets the download URL for the generated gif.
    /// </summary>
    public required string DownloadUrl { get; set; }
}
