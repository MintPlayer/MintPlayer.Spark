using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Media;

/// <summary>
/// Resolves the thumbnails of media URLs nobody asked about yet (#601 D11–D13): parses each URL, loads the cached
/// documents in one request, and asks the platform only for the missing ones.
/// </summary>
/// <remarks>
/// A platform without a resolver (files) gets no document. When one platform fails (timeout, 5xx, 429), the
/// answers already received are saved before the exception reaches Messaging, so the retry asks only for what
/// is still missing.
/// </remarks>
public partial class MediaUrlsAddedRecipient : IRecipient<MediaUrlsAdded>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IMediaThumbnailResolver> resolvers;
    [Inject] private readonly TimeProvider timeProvider;

    public async Task HandleAsync(MediaUrlsAdded message, CancellationToken cancellationToken = default)
    {
        var byPlatform = resolvers.ToDictionary(r => r.Platform, StringComparer.Ordinal);
        var wanted = message.Urls
            .Select(MediaUrl.TryParse)
            .OfType<MediaRef>()
            .Where(media => byPlatform.ContainsKey(media.Platform))
            .DistinctBy(MediaThumbnail.DocumentId)
            .ToList();
        if (wanted.Count == 0) return;

        var existing = await session.LoadAsync<MediaThumbnail>(wanted.Select(MediaThumbnail.DocumentId), cancellationToken);
        var resolved = new List<MediaThumbnail>();
        try
        {
            foreach (var media in wanted)
            {
                var id = MediaThumbnail.DocumentId(media);
                if (existing.TryGetValue(id, out var known) && known is not null) continue;

                var thumbnail = await byPlatform[media.Platform].ResolveAsync(media, cancellationToken);
                resolved.Add(new MediaThumbnail
                {
                    Id = id,
                    Platform = media.Platform,
                    PlatformId = media.Id,
                    ThumbnailUrl = thumbnail,
                    CheckedAt = timeProvider.GetUtcNow(),
                });
            }
        }
        finally
        {
            if (resolved.Count > 0)
            {
                foreach (var document in resolved)
                    await session.StoreAsync(document, document.Id, CancellationToken.None);
                await session.SaveChangesAsync(CancellationToken.None);
            }
        }
    }
}
