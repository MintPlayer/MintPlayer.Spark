using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Files.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands;
using Sparrow.Json;

namespace MintPlayer.Spark.Files.Services;

/// <summary>
/// Sweeps a store outside RavenDB (<see cref="ObjectFileStore"/>) for objects nothing holds any more (#700 D12). A store
/// outside the session can't commit with the entity, so a crash between the commit and the store's delete (or a save that
/// failed after its copy) leaves an object behind. An object older than <see cref="SparkFilesOptions.ReconcileGrace"/> is
/// deleted when:
/// <list type="bullet">
/// <item>it is staged, and its upload document is gone (expired), claimed or refused;</item>
/// <item>it is an entity's file, and the entity is gone or no longer holds that file id anywhere in its document.</item>
/// </list>
/// Documents are read raw, never through a typed or <c>object</c> load: the sweep must not depend on a CLR type, and every
/// <c>FileId</c> in the document counts, however deeply nested. Deletes are idempotent, so two instances sweeping at once
/// is harmless.
/// </summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
internal sealed partial class FilesReconciler
{
    private const int Batch = 256;

    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IOptions<SparkFilesOptions> options;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<FilesReconciler> logger;

    /// <summary>One sweep; returns the keys it deleted. Does nothing for a store that commits with the session.</summary>
    public async Task<IReadOnlyList<string>> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (fileStore is not ObjectFileStore)
            return [];
        var cutoff = clock.GetUtcNow() - options.Value.ReconcileGrace;
        var deleted = new List<string>();

        var staged = new List<(string Key, string Token)>();
        await foreach (var item in fileStore.ListAsync(ObjectFileStore.StagingRoot, cancellationToken))
        {
            if (item.LastModified <= cutoff && ObjectFileStore.TryParseStaging(item.Key, out var token))
                staged.Add((item.Key, token));
        }
        foreach (var chunk in staged.Chunk(Batch))
        {
            using var session = store.OpenAsyncSession();
            var uploads = await session.LoadAsync<SparkFileUpload>(chunk.Select(s => SparkFileUpload.IdFor(s.Token).Value), cancellationToken);
            foreach (var (key, token) in chunk)
            {
                var upload = uploads.GetValueOrDefault(SparkFileUpload.IdFor(token).Value);
                if (upload is null || upload.State is SparkFileUploadState.Claimed or SparkFileUploadState.Refused)
                    deleted.Add(key);
            }
        }

        var held = new List<(string Key, string EntityId, string FileId)>();
        await foreach (var item in fileStore.ListAsync(ObjectFileStore.EntitiesRoot, cancellationToken))
        {
            if (item.LastModified <= cutoff && ObjectFileStore.TryParseEntity(item.Key, out var entityId, out var fileId))
                held.Add((item.Key, entityId, fileId));
        }
        foreach (var chunk in held.Chunk(Batch))
        {
            var fileIds = await FileIdsAsync(chunk.Select(h => h.EntityId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
            foreach (var (key, entityId, fileId) in chunk)
            {
                if (!fileIds.TryGetValue(entityId, out var ids) || !ids.Contains(fileId))
                    deleted.Add(key);
            }
        }

        foreach (var key in deleted)
        {
            try
            {
                await fileStore.DeleteAsync(key, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The reconcile could not delete {Key}; the next sweep retries.", key);
            }
        }
        if (deleted.Count > 0)
            logger.LogInformation("The file reconcile deleted {Count} unheld object(s).", deleted.Count);
        return deleted;
    }

    /// <summary>Per existing document, every string held by a property named <c>FileId</c>, at any depth.</summary>
    private async Task<Dictionary<string, HashSet<string>>> FileIdsAsync(string[] ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var executor = store.GetRequestExecutor();
        var command = new GetDocumentsCommand(store.Conventions, ids, includes: null, metadataOnly: false);
        using (executor.ContextPool.AllocateOperationContext(out JsonOperationContext context))
        {
            await executor.ExecuteAsync(command, context, sessionInfo: null, cancellationToken);
            if (command.Result?.Results is not { } results)
                return result;
            foreach (var item in results)
            {
                if (item is not BlittableJsonReaderObject document
                    || !document.TryGet(Raven.Client.Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata)
                    || !metadata.TryGet(Raven.Client.Constants.Documents.Metadata.Id, out string id))
                    continue;
                var found = new HashSet<string>(StringComparer.Ordinal);
                Collect(document, found);
                result[id] = found;
            }
        }
        return result;
    }

    private static void Collect(object? value, HashSet<string> found)
    {
        switch (value)
        {
            case BlittableJsonReaderObject obj:
                var property = new BlittableJsonReaderObject.PropertyDetails();
                for (var i = 0; i < obj.Count; i++)
                {
                    obj.GetPropertyByIndex(i, ref property);
                    if (property.Name == Raven.Client.Constants.Documents.Metadata.Key)
                        continue;
                    if (string.Equals(property.Name, nameof(Abstractions.SparkFile.FileId), StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value?.ToString() is { Length: > 0 } fileId)
                            found.Add(fileId);
                        continue;
                    }
                    Collect(property.Value, found);
                }
                break;
            case BlittableJsonReaderArray array:
                foreach (var element in array)
                    Collect(element, found);
                break;
        }
    }
}

/// <summary>Runs <see cref="FilesReconciler"/> every <see cref="SparkFilesOptions.ReconcileInterval"/>, for a store outside RavenDB.</summary>
internal sealed partial class FilesReconcileService : BackgroundService
{
    [Inject] private readonly FilesReconciler reconciler;
    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly IOptions<SparkFilesOptions> options;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<FilesReconcileService> logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.ReconcileInterval;
        if (fileStore is not ObjectFileStore || interval <= TimeSpan.Zero)
            return;
        using var timer = new PeriodicTimer(interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await reconciler.ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The file reconcile failed; it retries next interval.");
            }
        }
    }
}
