using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.Template.Api.Models;

/// <summary>
/// Request payload used to generate a gif from a source video.
/// </summary>
public class CreateGifRequest
{
    /// <summary>
    /// Gets or sets the Jellyfin item id for the source video.
    /// </summary>
    [Required]
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the clip start time in seconds.
    /// </summary>
    [Range(0, int.MaxValue)]
    public double StartSeconds { get; set; }

    /// <summary>
    /// Gets or sets the clip length in seconds.
    /// </summary>
    [Range(1, int.MaxValue)]
    public double LengthSeconds { get; set; }

    /// <summary>
    /// Gets or sets the output gif width. If omitted or zero, plugin defaults are used.
    /// </summary>
    [Range(0, 4096)]
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the output gif fps. If omitted or zero, plugin defaults are used.
    /// </summary>
    [Range(0, 60)]
    public int Fps { get; set; }

    /// <summary>
    /// Gets or sets the subtitle stream index to burn into the generated gif.
    /// </summary>
    public int? SubtitleStreamIndex { get; set; }
}
