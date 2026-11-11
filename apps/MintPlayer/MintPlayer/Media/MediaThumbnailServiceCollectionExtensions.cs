using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MintPlayer.Media;

public static class MediaThumbnailServiceCollectionExtensions
{
    /// <summary>The thumbnail resolvers (#601 D13) and the oEmbed client the Vimeo, SoundCloud and Spotify ones share.</summary>
    public static IServiceCollection AddMediaThumbnails(this IServiceCollection services)
    {
        services.AddOptions<MediaThumbnailOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<OEmbedClient>(http =>
        {
            // A slow platform throws and is retried by Messaging (D12); it must not hold a handler for long.
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MintPlayer/1.0 (+https://mintplayer.com)");
        });
        services.AddScoped<IMediaThumbnailResolver, YouTubeThumbnailResolver>();
        services.AddScoped<IMediaThumbnailResolver, DailymotionThumbnailResolver>();
        services.AddScoped<IMediaThumbnailResolver, VimeoThumbnailResolver>();
        services.AddScoped<IMediaThumbnailResolver, SoundCloudThumbnailResolver>();
        services.AddScoped<IMediaThumbnailResolver, SpotifyThumbnailResolver>();
        return services;
    }
}
