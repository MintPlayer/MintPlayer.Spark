using MintPlayer.Media;

namespace MintPlayer.Tests.Media;

/// <summary>#601 D10: the platform and id the player plugins would find, which key the thumbnail cache.</summary>
public class MediaUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtube", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&t=42", "youtube", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "youtube", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "youtube", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/aqz-KE-bpKQ", "youtube", "aqz-KE-bpKQ")]
    [InlineData("https://m.youtube.com/shorts/aqz-KE-bpKQ", "youtube", "aqz-KE-bpKQ")]
    [InlineData("https://www.youtube.com/live/jfKfPfyJRdk", "youtube", "jfKfPfyJRdk")]
    [InlineData("https://vimeo.com/76979871", "vimeo", "76979871")]
    [InlineData("https://www.dailymotion.com/video/x8abcd1", "dailymotion", "x8abcd1")]
    [InlineData("https://soundcloud.com/forss/flickermood", "soundcloud", "https://soundcloud.com/forss/flickermood")]
    [InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT?si=0123", "spotify", "track:4cOdK2wGLETKBW3PvgPWqT")]
    [InlineData("https://open.spotify.com/episode/512ojhOuo1ktJprKbVcKyQ", "spotify", "episode:512ojhOuo1ktJprKbVcKyQ")]
    [InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT", "spotify", "track:4cOdK2wGLETKBW3PvgPWqT")]
    [InlineData("https://example.com/audio/song.mp3", "file", "https://example.com/audio/song.mp3")]
    public void Recognises_what_the_player_plays(string url, string platform, string id)
        => MediaUrl.TryParse(url).Should().Be(new MediaRef(platform, id));

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Rickrolling")]
    [InlineData("https://vimeo.com/channels/staff")]
    [InlineData("https://www.dailymotion.com/video/x8abcd1?playlist=x1")]
    [InlineData("https://open.spotify.com/album/1DFixLWuPkv3KT3TnV35m3")]
    [InlineData("")]
    [InlineData(null)]
    public void Has_no_platform_for_what_the_player_cannot_play(string? url)
        => MediaUrl.TryParse(url).Should().BeNull();
}
