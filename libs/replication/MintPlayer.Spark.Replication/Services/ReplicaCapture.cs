using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using Microsoft.Extensions.Options;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Stamps every published source document with its version on the way into the store (warehouse §4.2: "a version
/// stamp per save"). A store event rather than an <c>IBeforeSave</c>: it also stamps creations (whose metadata does
/// not exist while before-interceptors run) and raw session writes, which bypass interceptors. Spark saves with the
/// expected change vector, so the stamp is monotonic per document.
/// </summary>
internal sealed partial class ReplicationVersionStamp
{
    [Inject] private readonly ReplicationContractCatalog catalog;
    [Inject] private readonly TimeProvider timeProvider;

    /// <summary>Subscribes to <paramref name="store"/>'s before-store event, once.</summary>
    public void Attach(IDocumentStore store)
    {
        if (!catalog.All.Any())
            return;
        store.OnBeforeStore += (_, args) =>
        {
            if (args.Entity is null || !catalog.IsSource(args.Entity.GetType()))
                return;
            var previous = args.DocumentMetadata.TryGetValue(ReplicationMetadata.Version, out var value) ? ReplicationMetadata.Read(value) : 0;
            args.DocumentMetadata[ReplicationMetadata.Version] = ReplicationMetadata.Next(previous, timeProvider.GetUtcNow());
        };
    }
}

/// <summary>
/// The owner's outbox (warehouse WX1, HTTP transport): a committed save or delete of a published source document is
/// pushed to every HTTP consumer of its contracts. The framework stores the durable work in the save's own commit
/// (<see cref="IAfterSaveCommitted"/>, #482), so a crash right after the commit still pushes; this handler then fans
/// the change out as one message per consumer, each with its own retries.
/// </summary>
internal sealed partial class ReplicaPublishInterceptor : IBeforeDelete, IAfterSaveCommitted, IAfterDeleteCommitted
{
    [Inject] private readonly ReplicationContractCatalog catalog;
    [Inject] private readonly IReplicaFanOut fanOut;
    [Inject] private readonly TimeProvider timeProvider;

    public bool AppliesTo(Type entityType) => catalog.IsSource(entityType);

    /// <summary>Write-back applies and every other kind of write publish alike.</summary>
    public bool HandlesSync => true;

    /// <summary>
    /// Records the deletion's version stamp while the document still exists, for the tombstone the push carries: a
    /// re-creation stamps later, so a late tombstone never removes it.
    /// </summary>
    public ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        if (context.Session is IAsyncDocumentSession session && !context.IsReplaced)
        {
            var metadata = session.Advanced.GetMetadataFor(context.Entity);
            var previous = metadata.TryGetValue(ReplicationMetadata.Version, out var value) ? ReplicationMetadata.Read(value) : 0;
            context.Facts[ReplicationMetadata.DeletedVersionFact] = ReplicationMetadata.Next(previous, timeProvider.GetUtcNow())
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return ValueTask.CompletedTask;
    }

    public Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
        => fanOut.PublishAsync(change.EntityType, [change.Id], deletedVersion: null, cancellationToken);

    public Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        // A replaced delete (a soft delete) is a save: the document still exists and its state is pushed.
        long? version = change.Facts.TryGetValue(ReplicationMetadata.DeletedVersionFact, out var fact) ? ReplicationMetadata.Read(fact) : 0;
        return fanOut.PublishAsync(change.EntityType, [change.Id], change.IsReplaced ? null : version, cancellationToken);
    }
}

/// <summary>Turns a changed source document into one push message per HTTP consumer of its contracts.</summary>
internal interface IReplicaFanOut
{
    /// <param name="entityTypeName">The source document's CLR type (full name).</param>
    /// <param name="ids">The changed documents.</param>
    /// <param name="deletedVersion">The tombstone version when the documents were deleted (0: unknown); null for a save.</param>
    Task PublishAsync(string entityTypeName, IReadOnlyCollection<string> ids, long? deletedVersion, CancellationToken cancellationToken);

    /// <summary>The same, for every contract named <paramref name="contract"/>.</summary>
    Task PublishContractAsync(string contract, IReadOnlyCollection<string> ids, CancellationToken cancellationToken);
}

internal sealed partial class ReplicaFanOut : IReplicaFanOut
{
    [Inject] private readonly ReplicationContractCatalog catalog;
    [Inject] private readonly IReplicaSubscriptions subscriptions;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly TimeProvider timeProvider;

    public async Task PublishAsync(string entityTypeName, IReadOnlyCollection<string> ids, long? deletedVersion, CancellationToken cancellationToken)
    {
        var contracts = catalog.All
            .Where(c => IsAssignable(c.Contract.SourceType, entityTypeName))
            .Select(c => c.Name)
            .Distinct(StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            if (options.Value.DirtySetContracts.Contains(contract))
                await MarkDirtyAsync(contract, ids, cancellationToken);
            else
                await PublishCoreAsync(contract, ids, deletedVersion, cancellationToken);
        }
    }

    /// <summary>
    /// A high-churn contract: the change only marks the documents dirty (one marker per document, overwritten by every
    /// further change), and <see cref="ReplicaDirtySetFlushJob"/> pushes their latest state once.
    /// </summary>
    private async Task MarkDirtyAsync(string contract, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var id in ids)
        {
            var markerId = ReplicaDirtyMarker.For(contract, id);
            await session.StoreAsync(new ReplicaDirtyMarker { Id = markerId, Contract = contract, DocumentId = id, MarkedAtUtc = now }, markerId, cancellationToken);
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    public Task PublishContractAsync(string contract, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
        => PublishCoreAsync(contract, ids, deletedVersion: null, cancellationToken);

    private async Task PublishCoreAsync(string contract, IReadOnlyCollection<string> ids, long? deletedVersion, CancellationToken cancellationToken)
    {
        var settings = options.Value.Push;
        foreach (var consumer in await subscriptions.ConsumersAsync(contract, cancellationToken))
        {
            if (consumer.Transport != ReplicationTransport.Http)
                continue;

            foreach (var batch in ids.Chunk(Math.Max(1, settings.BatchSize)))
            {
                await messageBus.BroadcastAsync(new ReplicaPushMessage
                {
                    Consumer = consumer.Module,
                    Contract = contract,
                    Version = consumer.Version,
                    Ids = [.. batch],
                    DeletedVersion = deletedVersion,
                }, new BroadcastOptions { MaxAttempts = settings.MaxAttempts }, cancellationToken);
            }
        }
    }

    private static bool IsAssignable(Type sourceType, string entityTypeName)
    {
        for (Type? current = Type.GetType(entityTypeName) ?? FindLoaded(entityTypeName); current is not null; current = current.BaseType)
        {
            if (current == sourceType)
                return true;
        }
        return string.Equals(sourceType.FullName, entityTypeName, StringComparison.Ordinal);
    }

    private static Type? FindLoaded(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, throwOnError: false)).FirstOrDefault(t => t is not null);
}

/// <summary>
/// <see cref="IReplicaPublisher"/>: records a raw write in the write's own session as the same durable work a
/// pipeline save produces, so it is pushed after the commit — or not at all.
/// </summary>
internal sealed partial class ReplicaPublisher : IReplicaPublisher
{
    [Inject] private readonly ISparkAfterCommitOutbox outbox;
    [Inject] private readonly TimeProvider timeProvider;

    public Task MarkChangedAsync(object session, Type sourceType, string id, CancellationToken cancellationToken = default)
        => outbox.EnqueueAsync(session, new SparkAfterCommitWork
        {
            InterceptorType = typeof(ReplicaPublishInterceptor).FullName!,
            IsDelete = false,
            Change = new SparkCommittedChange
            {
                EntityType = sourceType.FullName!,
                Id = id,
                Operation = PersistentObjectOperation.Save,
                IsSystemContext = true,
                OccurredAt = timeProvider.GetUtcNow(),
            },
        }, cancellationToken);
}
