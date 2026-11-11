using MintPlayer.Actions;
using MintPlayer.Entities;
using MintPlayer.Media;

namespace MintPlayer.Tests.Media;

/// <summary>#601 D16: which medium a song page's header shows, and D11: which media a save queues.</summary>
public class SongHeroTests
{
    private const string Video = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const string OtherVideo = "https://youtu.be/aqz-KE-bpKQ";
    private const string Vimeo = "https://vimeo.com/76979871";
    private const string Dailymotion = "https://www.dailymotion.com/video/x8abcd1";
    private const string Spotify = "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT";
    private const string Wikipedia = "https://en.wikipedia.org/wiki/Rickrolling";

    private static readonly Dictionary<string, MediumType> Types = new()
    {
        ["MediumTypes/1"] = new() { Id = "MediumTypes/1", Name = "Official video", Visible = true },
        ["MediumTypes/15"] = new() { Id = "MediumTypes/15", Name = "Lyrics source", Visible = false },
    };

    private static Medium M(string url, string? typeId = "MediumTypes/1") => new() { Value = url, TypeId = typeId };

    private static Dictionary<string, MediaThumbnail> Thumbnails(params string[] urls) => urls.ToDictionary(
        url => MediaThumbnail.DocumentId(MediaUrl.TryParse(url)!.Value),
        url => new MediaThumbnail { ThumbnailUrl = $"https://img.example/{url.GetHashCode():x}.jpg" });

    [Fact]
    public void Takes_the_first_medium_in_the_editors_order()
    {
        var thumbnails = Thumbnails(Video, OtherVideo);
        var hero = SongHero.Pick([M(Wikipedia), M(OtherVideo), M(Video)], Types, thumbnails);
        hero!.MediumUrl.Should().Be(OtherVideo);
        hero.Thumbnail.Should().Be(thumbnails[MediaThumbnail.DocumentId(MediaUrl.TryParse(OtherVideo)!.Value)].ThumbnailUrl);
    }

    [Fact]
    public void Skips_a_medium_of_a_type_hidden_from_visitors()
        => SongHero.Pick([M(OtherVideo, "MediumTypes/15"), M(Video)], Types, Thumbnails(Video, OtherVideo))!
            .MediumUrl.Should().Be(Video);

    [Fact]
    public void Counts_an_untyped_or_unknown_type_as_shown()
    {
        SongHero.Pick([M(Video, null)], Types, Thumbnails(Video))!.MediumUrl.Should().Be(Video);
        SongHero.Pick([M(Video, "MediumTypes/404")], Types, Thumbnails(Video))!.MediumUrl.Should().Be(Video);
    }

    [Fact]
    public void Skips_the_platforms_the_site_cannot_play()
        // Dailymotion's player does not play today (S5); the site loads no Spotify plugin (video-apis.ts).
        => SongHero.Pick([M(Dailymotion), M(Spotify), M(Vimeo)], Types, Thumbnails(Dailymotion, Spotify, Vimeo))!
            .MediumUrl.Should().Be(Vimeo);

    [Fact]
    public void Skips_a_refused_or_unresolved_thumbnail()
    {
        var thumbnails = Thumbnails(Video);
        thumbnails[MediaThumbnail.DocumentId(MediaUrl.TryParse(OtherVideo)!.Value)] = new MediaThumbnail { ThumbnailUrl = null };
        SongHero.Pick([M(OtherVideo), M(Vimeo), M(Video)], Types, thumbnails)!.MediumUrl.Should().Be(Video);
    }

    [Fact]
    public void Has_no_header_without_a_playable_thumbnail()
        => SongHero.Pick([M(Wikipedia), M(Dailymotion)], Types, Thumbnails(Dailymotion)).Should().BeNull();

    [Fact]
    public void Loads_only_the_thumbnails_a_header_can_use()
        => SongHero.CandidateIds([M(Wikipedia), M(Video), M(Dailymotion), M(Video), M(Vimeo)])
            .Should().Equal(["MediaThumbnails/youtube/dQw4w9WgXcQ", "MediaThumbnails/vimeo/76979871"]);

    [Fact]
    public void A_save_queues_only_the_playable_media_it_adds()
    {
        var before = new Song { Media = [M(Video), M(Wikipedia)] };
        var after = new Song { Media = [M(Video), M(Wikipedia), M(Vimeo), M(Spotify), M(Vimeo), M("https://genius.com/x")] };

        SongActions.AddedMediaUrls(before, after).Should().Equal([Vimeo, Spotify]);
        SongActions.AddedMediaUrls(null, before).Should().Equal([Video]);
        SongActions.AddedMediaUrls(after, after).Should().BeEmpty();
    }
}
