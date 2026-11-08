using System.Text.RegularExpressions;

namespace MintPlayer.Media;

/// <summary>A medium's platform and its id there, as the player plugin names them.</summary>
/// <param name="Platform">The plugin's <c>id</c>: <c>youtube</c>, <c>vimeo</c>, <c>dailymotion</c>, <c>soundcloud</c>, <c>spotify</c> or <c>file</c>.</param>
/// <param name="Id">What the plugin's <c>match2id</c> builds: the video id, the whole URL (SoundCloud, files) or <c>track:{id}</c> / <c>episode:{id}</c> (Spotify).</param>
public readonly record struct MediaRef(string Platform, string Id);

/// <summary>
/// Which platform a medium's URL plays on, and its id there: the server-side copy of the player's URL recognition
/// (<c>findApis(url, plugins)</c> in mintplayer-ng-video-player). The patterns are the plugin api services'
/// <c>urlRegexes</c>, verbatim, in the plugins' order; unanchored unless the source regex is anchored, like
/// <c>RegExp.exec</c>. The thumbnail cache keys on the result (#601 D9/D10), and the migration's
/// <see cref="IsPlayable"/> check uses it too.
/// </summary>
/// <remarks>
/// It drifts when the plugin library adds or changes a platform. An unknown URL is not an error: it just has no
/// platform, so no thumbnail.
/// </remarks>
public static class MediaUrl
{
    public const string YouTube = "youtube";
    public const string Vimeo = "vimeo";
    public const string Dailymotion = "dailymotion";
    public const string SoundCloud = "soundcloud";
    public const string Spotify = "spotify";
    public const string File = "file";

    private static readonly (string Platform, Regex Pattern)[] Patterns =
    [
        (YouTube, new(@"http[s]{0,1}://(www\.){0,1}youtube\.com/watch\?v=(?<id>[^&]+)")),
        (YouTube, new(@"http[s]{0,1}://m\.youtube\.com/watch\?v=(?<id>[^&]+)")),
        (YouTube, new(@"http[s]{0,1}://(www\.){0,1}youtu\.be/(?<id>[^&?]+)")),
        (YouTube, new(@"http[s]{0,1}://(www\.){0,1}youtube\.com/shorts/(?<id>[^&?]+)")),
        (YouTube, new(@"http[s]{0,1}://m\.youtube\.com/shorts/(?<id>[^&?]+)")),
        (YouTube, new(@"http[s]{0,1}://(www\.){0,1}youtube\.com/live/(?<id>[^&?]+)")),
        (Vimeo, new(@"http[s]{0,1}://(www\.){0,1}vimeo\.com/(?<id>[0-9]+)$")),
        (Dailymotion, new(@"http[s]{0,1}://(www\.){0,1}dailymotion\.com/video/(?<id>[0-9A-Za-z]+)$")),
        (SoundCloud, new(@"(?<id>http[s]{0,1}://(www\.){0,1}soundcloud\.com/.+)$")),
        // The plugin excludes `?` from the id, or every share link (`?si=...`) would give a different id.
        (Spotify, new(@"http[s]{0,1}://open\.spotify\.com/(?<type>track|episode)/(?<id>[^&/?]+)")),
        (Spotify, new(@"spotify:(?<type>track|episode):(?<id>[0-9A-Za-z]+)")),
        (File, new(@"(?<id>https?://.+\.(?<audiotype>mp4a|mpga|mp2a|weba|m4a|m4b|m2a|m3a|mp2|mp3|wav|aac|oga|spx))(?![a-z0-9])")),
        (File, new(@"(?<id>https?://.+\.(?<videotype>webm|mp4|ogg|ogv|mov|m4v))(?![a-z0-9])")),
    ];

    /// <summary>The platform and id of <paramref name="url"/>, or null when no player plugin recognises it.</summary>
    public static MediaRef? TryParse(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        foreach (var (platform, pattern) in Patterns)
        {
            var match = pattern.Match(url);
            if (!match.Success) continue;
            var id = match.Groups["id"].Value;
            // The Spotify plugin's match2id: `spotify:{type}:{id}`; the platform is the key's own segment.
            return platform == Spotify
                ? new MediaRef(platform, $"{match.Groups["type"].Value}:{id}")
                : new MediaRef(platform, id);
        }
        return null;
    }

    public static bool IsPlayable(string? url) => TryParse(url) is not null;
}
