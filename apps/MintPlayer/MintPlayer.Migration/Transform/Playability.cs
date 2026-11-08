using MintPlayer.Media;

namespace MintPlayer.Migration.Transform;

/// <summary>
/// Server-side mirror of the client's playability check (<c>MediaPlayabilityService</c> →
/// <c>findApis(url, VIDEO_PLAYER_PLUGINS)</c>). The karaoke display matches a <c>LyricsTiming</c> by the
/// URL the player is playing, and <c>PlaylistPlaybackService.firstPlayable</c> plays the first playable
/// URL of <c>Song.Media</c> in array order — so a migrated timeline is keyed to that URL.
/// The plugins' URL patterns live in <see cref="MediaUrl"/> (#601 D10).
/// </summary>
public static class Playability
{
    public static bool IsPlayable(string? url) => MediaUrl.IsPlayable(url);

    public static string? FirstPlayable(IEnumerable<string> urls) => urls.FirstOrDefault(IsPlayable);
}
