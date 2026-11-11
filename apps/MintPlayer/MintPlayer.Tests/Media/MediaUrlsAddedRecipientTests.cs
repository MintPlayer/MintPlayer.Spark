using MintPlayer.Media;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Tests.Media;

/// <summary>#601 D11/D12: only unknown media are asked about, refusals are kept, failures are retried.</summary>
public sealed class MediaUrlsAddedRecipientTests : SparkTestDriver
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A platform that answers from a table, and counts what it is asked.</summary>
    private sealed class TableResolver(string platform, Func<MediaRef, string?> answer) : IMediaThumbnailResolver
    {
        public List<string> Asked { get; } = [];
        public string Platform => platform;

        public Task<string?> ResolveAsync(MediaRef media, CancellationToken cancellationToken)
        {
            Asked.Add(media.Id);
            return Task.FromResult(answer(media));
        }
    }

    private async Task HandleAsync(MediaUrlsAdded message, params IMediaThumbnailResolver[] resolvers)
    {
        using var session = Store.OpenAsyncSession();
        await new MediaUrlsAddedRecipient(session, resolvers, new FixedTime()).HandleAsync(message);
    }

    private async Task<MediaThumbnail?> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<MediaThumbnail>(id);
    }

    [Fact]
    public async Task Stores_each_new_video_under_its_platform_id()
    {
        var youtube = new TableResolver(MediaUrl.YouTube, m => $"https://i.ytimg.com/vi/{m.Id}/hqdefault.jpg");

        await HandleAsync(new MediaUrlsAdded([
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "https://youtu.be/dQw4w9WgXcQ",              // the same video: asked once
            "https://en.wikipedia.org/wiki/Rickrolling", // no platform
            "https://example.com/audio/song.mp3",        // a platform without a resolver
        ]), youtube);

        youtube.Asked.Should().Equal(["dQw4w9WgXcQ"]);
        var stored = await LoadAsync("MediaThumbnails/youtube/dQw4w9WgXcQ");
        stored!.ThumbnailUrl.Should().Be("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg");
        stored.Platform.Should().Be("youtube");
        stored.PlatformId.Should().Be("dQw4w9WgXcQ");
        stored.CheckedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Never_asks_again_about_a_known_video_nor_a_refused_one()
    {
        var vimeo = new TableResolver(MediaUrl.Vimeo, m => m.Id == "1" ? null : $"https://i.vimeocdn.com/video/{m.Id}");
        await HandleAsync(new MediaUrlsAdded(["https://vimeo.com/1", "https://vimeo.com/76979871"]), vimeo);
        (await LoadAsync("MediaThumbnails/vimeo/1"))!.ThumbnailUrl.Should().BeNull();

        await HandleAsync(new MediaUrlsAdded(["https://vimeo.com/1", "https://vimeo.com/76979871", "https://vimeo.com/22439234"]), vimeo);

        vimeo.Asked.Should().Equal(["1", "76979871", "22439234"]);
    }

    [Fact]
    public async Task Keeps_the_answers_it_has_when_a_platform_fails_so_the_retry_asks_only_the_rest()
    {
        var youtube = new TableResolver(MediaUrl.YouTube, m => $"https://i.ytimg.com/vi/{m.Id}/hqdefault.jpg");
        var failing = new TableResolver(MediaUrl.Spotify, _ => throw new HttpRequestException("503"));

        var act = () => HandleAsync(new MediaUrlsAdded([
            "https://youtu.be/dQw4w9WgXcQ",
            "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT",
        ]), youtube, failing);

        await act.Should().ThrowAsync<HttpRequestException>();
        (await LoadAsync("MediaThumbnails/youtube/dQw4w9WgXcQ")).Should().NotBeNull();
        (await LoadAsync("MediaThumbnails/spotify/track:4cOdK2wGLETKBW3PvgPWqT")).Should().BeNull();
    }
}
