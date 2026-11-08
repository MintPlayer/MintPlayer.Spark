using System.Net;
using Microsoft.Extensions.Options;
using MintPlayer.Media;
using MintPlayer.Spark.Messaging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace MintPlayer.Tests.Media;

/// <summary>
/// #601 D12/D13: the oEmbed resolvers against the platforms' real answers (recorded in <c>Fixtures/oembed</c>), and
/// the derived ones. A definitive "no" is null; a failed question throws, so Messaging retries it.
/// </summary>
public sealed class MediaThumbnailResolverTests : IDisposable
{
    private readonly WireMockServer server = WireMockServer.Start();

    public void Dispose() => server.Stop();

    private IOptions<MediaThumbnailOptions> Endpoints => Options.Create(new MediaThumbnailOptions
    {
        VimeoOEmbed = new Uri($"{server.Url}/vimeo/api/oembed.json"),
        SoundCloudOEmbed = new Uri($"{server.Url}/soundcloud/oembed"),
        SpotifyOEmbed = new Uri($"{server.Url}/spotify/oembed"),
    });

    private OEmbedClient Client => new(new HttpClient());

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "oembed", name));

    private void Answer(string path, string? url, int status, string body = "")
    {
        var request = Request.Create().WithPath(path).UsingGet().WithParam("format", "json");
        if (url is not null) request = request.WithParam("url", url);
        server.Given(request).RespondWith(Response.Create().WithStatusCode(status).WithBody(body));
    }

    [Fact]
    public async Task Vimeo_asks_for_a_header_sized_thumbnail_of_the_video_page()
    {
        server.Given(Request.Create().WithPath("/vimeo/api/oembed.json").UsingGet()
                .WithParam("url", "https://vimeo.com/76979871").WithParam("width", "1280"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Fixture("vimeo-76979871.json")));

        var thumbnail = await new VimeoThumbnailResolver(Client, Endpoints)
            .ResolveAsync(new MediaRef(MediaUrl.Vimeo, "76979871"), default);

        thumbnail.Should().StartWith("https://i.vimeocdn.com/video/452001751-");
    }

    [Fact]
    public async Task SoundCloud_asks_about_the_track_url_itself()
    {
        Answer("/soundcloud/oembed", "https://soundcloud.com/forss/flickermood", 200, Fixture("soundcloud-flickermood.json"));

        var thumbnail = await new SoundCloudThumbnailResolver(Client, Endpoints)
            .ResolveAsync(new MediaRef(MediaUrl.SoundCloud, "https://soundcloud.com/forss/flickermood"), default);

        thumbnail.Should().Be("https://i1.sndcdn.com/artworks-000067273316-smsiqx-t500x500.jpg");
    }

    [Fact]
    public async Task Spotify_asks_about_the_track_page()
    {
        Answer("/spotify/oembed", "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", 200, Fixture("spotify-track.json"));

        var thumbnail = await new SpotifyThumbnailResolver(Client, Endpoints)
            .ResolveAsync(new MediaRef(MediaUrl.Spotify, "track:4cOdK2wGLETKBW3PvgPWqT"), default);

        thumbnail.Should().Be("https://image-cdn-fa.spotifycdn.com/image/ab67616d00001e02baf89eb11ec7c657805d2da0");
    }

    [Theory]
    [InlineData(404)]   // deleted: Vimeo's real body is "404 Not Found"
    [InlineData(403)]   // private, or embedding refused
    [InlineData(401)]
    [InlineData(400)]
    public async Task A_definitive_no_is_null(int status)
    {
        Answer("/vimeo/api/oembed.json", null, status, Fixture("vimeo-404.json"));

        var thumbnail = await new VimeoThumbnailResolver(Client, Endpoints)
            .ResolveAsync(new MediaRef(MediaUrl.Vimeo, "1"), default);

        thumbnail.Should().BeNull();
    }

    [Fact]
    public async Task A_server_error_throws_so_the_message_is_retried()
    {
        Answer("/spotify/oembed", null, 503);

        var act = () => new SpotifyThumbnailResolver(Client, Endpoints)
            .ResolveAsync(new MediaRef(MediaUrl.Spotify, "track:4cOdK2wGLETKBW3PvgPWqT"), default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Too_many_requests_parks_the_message_for_as_long_as_asked()
    {
        server.Given(Request.Create().WithPath("/soundcloud/oembed").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.TooManyRequests).WithHeader("Retry-After", "120"));

        var act = () => new SoundCloudThumbnailResolver(Client, Endpoints)
            .ResolveAsync(new MediaRef(MediaUrl.SoundCloud, "https://soundcloud.com/forss/flickermood"), default);

        (await act.Should().ThrowAsync<RetryAfterException>()).Which.Delay.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task A_thumbnail_that_is_not_https_is_not_kept()
    {
        Answer("/spotify/oembed", null, 200, """{"thumbnail_url":"javascript:alert(1)"}""");

        var thumbnail = await new SpotifyThumbnailResolver(Client, Endpoints)
            .ResolveAsync(new MediaRef(MediaUrl.Spotify, "track:4cOdK2wGLETKBW3PvgPWqT"), default);

        thumbnail.Should().BeNull();
    }

    [Fact]
    public async Task YouTube_and_Dailymotion_are_derived_without_a_request()
    {
        (await new YouTubeThumbnailResolver().ResolveAsync(new MediaRef(MediaUrl.YouTube, "dQw4w9WgXcQ"), default))
            .Should().Be("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg");
        (await new DailymotionThumbnailResolver().ResolveAsync(new MediaRef(MediaUrl.Dailymotion, "x8abcd1"), default))
            .Should().Be("https://www.dailymotion.com/thumbnail/video/x8abcd1");
    }
}
