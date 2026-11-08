using MintPlayer.Entities;
using MintPlayer.Media;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;

namespace MintPlayer.Migrations;

/// <summary>
/// Queues the thumbnails of every song's existing media once (#601 D14), so the song pages have their header
/// right after the deploy that introduces it, not after each song's next edit.
/// </summary>
/// <remarks>
/// It only enqueues: the platforms are asked by <see cref="MediaUrlsAddedRecipient"/>, because a throwing
/// migration aborts startup. Streams by id prefix, so it needs no index.
/// </remarks>
public partial class M_202611081200_BackfillMediaThumbnails : ISparkMigration
{
    public static long Version => 202611081200;
    public static string? Description => "Queue the thumbnails of every song's media";

    /// <summary>URLs per message: one message's failure retries at most this many platform requests.</summary>
    internal const int BatchSize = 100;

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<M_202611081200_BackfillMediaThumbnails> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        using (var session = store.OpenAsyncSession())
        {
            await using var stream = await session.Advanced.StreamAsync<Song>(startsWith: "Songs/", token: cancellationToken);
            while (await stream.MoveNextAsync())
            {
                if (!string.Equals(stream.Current.Metadata.GetString("@collection"), "Songs", StringComparison.Ordinal)) continue;
                foreach (var medium in stream.Current.Document.Media)
                    if (MediaUrl.TryParse(medium.Value) is not null)
                        urls.Add(medium.Value);
            }
        }

        foreach (var batch in urls.Chunk(BatchSize))
            await messageBus.BroadcastAsync(new MediaUrlsAdded(batch), cancellationToken);

        logger.LogInformation("Queued the thumbnails of {Count} media URLs", urls.Count);
    }
}
