using MintPlayer.Media;

namespace MintPlayer.Media;

/// <summary>
/// The thumbnail of one video or track on a platform, cached so a song page never asks the platform (#601 D9).
/// One document per platform video, whichever subject links it: a song, its artist and a playlist share it.
/// </summary>
/// <remarks>
/// Written only by <see cref="MediaUrlsAddedRecipient"/>; it is not a persistent object and has no model.
/// <see cref="ThumbnailUrl"/> null means the platform said no (deleted, private, refused), which is permanent:
/// delete the document to have it asked again (D12).
/// </remarks>
public sealed class MediaThumbnail
{
    public const string Collection = "MediaThumbnails";

    public string? Id { get; set; }

    /// <summary>The player plugin's id: <see cref="MediaUrl.YouTube"/>, <see cref="MediaUrl.Vimeo"/>, ….</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>The id the plugin builds (see <see cref="MediaRef.Id"/>).</summary>
    public string PlatformId { get; set; } = string.Empty;

    /// <summary>An https image URL, or null when the platform has no thumbnail for it.</summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>When the platform was asked, so a later sweep can re-ask old refusals.</summary>
    public DateTimeOffset CheckedAt { get; set; }

    /// <summary>The natural id, <c>MediaThumbnails/{platform}/{platformId}</c>: the same video always maps to the same document.</summary>
    public static string DocumentId(MediaRef media) => $"{Collection}/{media.Platform}/{media.Id}";
}

/// <summary>A save added or kept these media URLs; their thumbnails are resolved if not known yet (#601 D11).</summary>
/// <param name="Urls">The URLs as typed, unparsed: the recipient decides which platforms it knows.</param>
public sealed record MediaUrlsAdded(string[] Urls);
