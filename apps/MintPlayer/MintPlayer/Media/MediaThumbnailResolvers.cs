using System.Net;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Newtonsoft.Json.Linq;

namespace MintPlayer.Media;

/// <summary>
/// Finds the thumbnail of a video on one platform (#601 D13). Returns null when the platform says no for good
/// (deleted, private, refused), and throws when the question could not be asked (timeout, 5xx, 429), so the
/// message is retried (D12).
/// </summary>
public interface IMediaThumbnailResolver
{
    /// <summary>The <see cref="MediaRef.Platform"/> it resolves.</summary>
    string Platform { get; }

    Task<string?> ResolveAsync(MediaRef media, CancellationToken cancellationToken);
}

/// <summary>
/// YouTube's thumbnails are at a fixed address. <c>hqdefault</c> (480×360) exists for every video;
/// <c>maxresdefault</c> is a 404 for older uploads (S3).
/// </summary>
public sealed class YouTubeThumbnailResolver : IMediaThumbnailResolver
{
    public string Platform => MediaUrl.YouTube;

    public Task<string?> ResolveAsync(MediaRef media, CancellationToken cancellationToken)
        => Task.FromResult<string?>($"https://i.ytimg.com/vi/{Uri.EscapeDataString(media.Id)}/hqdefault.jpg");
}

/// <summary>Dailymotion redirects its fixed thumbnail address to the current image (S3).</summary>
public sealed class DailymotionThumbnailResolver : IMediaThumbnailResolver
{
    public string Platform => MediaUrl.Dailymotion;

    public Task<string?> ResolveAsync(MediaRef media, CancellationToken cancellationToken)
        => Task.FromResult<string?>($"https://www.dailymotion.com/thumbnail/video/{Uri.EscapeDataString(media.Id)}");
}

/// <summary>Where the oEmbed endpoints are. Only the tests change them.</summary>
public sealed class MediaThumbnailOptions
{
    public Uri VimeoOEmbed { get; set; } = new("https://vimeo.com/api/oembed.json");
    public Uri SoundCloudOEmbed { get; set; } = new("https://soundcloud.com/oembed");
    public Uri SpotifyOEmbed { get; set; } = new("https://open.spotify.com/oembed");
}

/// <summary>
/// The oEmbed request the Vimeo, SoundCloud and Spotify resolvers share: <c>GET {endpoint}?format=json&amp;url={page}</c>,
/// then the response's <c>thumbnail_url</c>.
/// </summary>
public sealed partial class OEmbedClient
{
    [Inject] private readonly HttpClient http;

    /// <summary>
    /// The <c>thumbnail_url</c> of <paramref name="pageUrl"/>, or null when the platform answers that it has none to
    /// give: 404 (deleted), 401/403 (private, embedding refused) or 400 (not a page it knows).
    /// </summary>
    /// <exception cref="RetryAfterException">429: the platform asked to be asked later.</exception>
    /// <exception cref="HttpRequestException">Any other failure, so the message is retried.</exception>
    /// <param name="extraQuery">More query parameters, already escaped (Vimeo's <c>width</c>).</param>
    public async Task<string?> ThumbnailAsync(Uri endpoint, string pageUrl, CancellationToken cancellationToken, string? extraQuery = null)
    {
        var query = $"format=json&url={Uri.EscapeDataString(pageUrl)}";
        if (extraQuery is not null) query += "&" + extraQuery;
        var uri = new UriBuilder(endpoint) { Query = query }.Uri;
        using var response = await http.GetAsync(uri, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest:
                return null;
            case HttpStatusCode.TooManyRequests:
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(1));
                throw new RetryAfterException(delay, $"{endpoint.Host} answered 429");
        }
        response.EnsureSuccessStatusCode();

        var body = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var thumbnail = body.Value<string>("thumbnail_url");
        // Rendered as an <img src> on a public page: only an absolute https image, never another scheme.
        return Uri.TryCreate(thumbnail, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed.AbsoluteUri
            : null;
    }
}

public sealed partial class VimeoThumbnailResolver : IMediaThumbnailResolver
{
    [Inject] private readonly OEmbedClient oEmbed;
    [Inject] private readonly IOptions<MediaThumbnailOptions> options;

    public string Platform => MediaUrl.Vimeo;

    public Task<string?> ResolveAsync(MediaRef media, CancellationToken cancellationToken)
        // Without a width Vimeo answers a 295×166 thumbnail, too small for a page header.
        => oEmbed.ThumbnailAsync(options.Value.VimeoOEmbed, $"https://vimeo.com/{media.Id}", cancellationToken, "width=1280");
}

/// <summary>SoundCloud's id is the whole track URL (the plugin plays the page itself).</summary>
public sealed partial class SoundCloudThumbnailResolver : IMediaThumbnailResolver
{
    [Inject] private readonly OEmbedClient oEmbed;
    [Inject] private readonly IOptions<MediaThumbnailOptions> options;

    public string Platform => MediaUrl.SoundCloud;

    public Task<string?> ResolveAsync(MediaRef media, CancellationToken cancellationToken)
        => oEmbed.ThumbnailAsync(options.Value.SoundCloudOEmbed, media.Id, cancellationToken);
}

/// <summary>Spotify's id is <c>track:{id}</c> or <c>episode:{id}</c>; its page is <c>open.spotify.com/{type}/{id}</c>.</summary>
public sealed partial class SpotifyThumbnailResolver : IMediaThumbnailResolver
{
    [Inject] private readonly OEmbedClient oEmbed;
    [Inject] private readonly IOptions<MediaThumbnailOptions> options;

    public string Platform => MediaUrl.Spotify;

    public Task<string?> ResolveAsync(MediaRef media, CancellationToken cancellationToken)
        => oEmbed.ThumbnailAsync(options.Value.SpotifyOEmbed, $"https://open.spotify.com/{media.Id.Replace(':', '/')}", cancellationToken);
}
