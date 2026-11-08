using MintPlayer.Entities;

namespace MintPlayer.Media;

/// <summary>The image and the URL a song page's header shows and plays (#601 D16).</summary>
public sealed record SongHero(string Thumbnail, string MediumUrl)
{
    /// <summary>
    /// The platforms the header may show: the ones the site's player has (ClientApp <c>player/video-apis.ts</c>:
    /// YouTube, Vimeo, SoundCloud, Dailymotion), without Dailymotion, whose player does not play today (S5).
    /// Spotify and files have no player plugin in the site, so their thumbnails are cached but never a header.
    /// </summary>
    /// <remarks>Add Dailymotion back when its player is fixed, and Spotify when the site loads its plugin.</remarks>
    public static readonly IReadOnlySet<string> Platforms = new HashSet<string>(StringComparer.Ordinal)
    {
        MediaUrl.YouTube, MediaUrl.Vimeo, MediaUrl.SoundCloud,
    };

    /// <summary>The thumbnail ids worth loading for <paramref name="media"/>: those of the header platforms.</summary>
    public static IEnumerable<string> CandidateIds(IEnumerable<Medium> media)
        => media.Select(m => MediaUrl.TryParse(m.Value))
            .OfType<MediaRef>()
            .Where(r => Platforms.Contains(r.Platform))
            .Select(MediaThumbnail.DocumentId)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The first medium, in the order the editor put them, on a header platform, of a type shown to visitors and
    /// with a known thumbnail; null when there is none. The same for every visitor.
    /// </summary>
    /// <param name="types">Medium types by id, as <c>LoadAsync</c> returns them (null values for missing documents); a missing type counts as shown.</param>
    /// <param name="thumbnails">Cached thumbnails by document id, likewise; missing means not resolved yet.</param>
    public static SongHero? Pick(
        IEnumerable<Medium> media,
        IReadOnlyDictionary<string, MediumType> types,
        IReadOnlyDictionary<string, MediaThumbnail> thumbnails)
    {
        foreach (var medium in media)
        {
            if (MediaUrl.TryParse(medium.Value) is not { } parsed || !Platforms.Contains(parsed.Platform)) continue;
            if (medium.TypeId is { } typeId && types.TryGetValue(typeId, out var type) && type is { Visible: false }) continue;
            if (!thumbnails.TryGetValue(MediaThumbnail.DocumentId(parsed), out var thumbnail)
                || thumbnail?.ThumbnailUrl is not { } url) continue;
            return new SongHero(url, medium.Value);
        }
        return null;
    }
}
