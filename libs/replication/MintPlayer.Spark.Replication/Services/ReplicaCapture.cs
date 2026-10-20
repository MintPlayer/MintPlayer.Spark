using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Owner, HTTP transport (warehouse §4.2.3 "Capture"): a committed save or delete of a model type is fanned out as one
/// push per consumer that receives its collection over HTTP. The framework stores the work in the save's own commit
/// (<see cref="IAfterSaveCommitted"/>, #482), so a crash right after the commit still pushes, without a subscription.
/// <para>
/// <see cref="ISparkInterceptor.AppliesTo"/> is cached per type and cannot know today's subscriptions. It claims only
/// the model types whose collection <c>security.json</c> grants <c>Replicate/{collection}</c> on, since no consumer can
/// subscribe to any other (the register endpoint checks that grant); the handler drops a change nobody subscribes to.
/// A grant added by a security reload is captured after the next restart; until then the reconcile carries it.
/// </para>
/// </summary>
internal sealed partial class ReplicaPublishInterceptor : IAfterSaveCommitted, IAfterDeleteCommitted
{
    [Inject] private readonly IReplicaFanOut fanOut;
    [Inject] private readonly ReplicaCatalog catalog;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IReplicaSourceTypes sourceTypes;
    [Inject] private readonly ISecurityConfigurationLoader securityConfiguration;

    public bool AppliesTo(Type entityType)
        => !catalog.IsReplica(entityType)
            && modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name) is not null
            && IsReplicable(sourceTypes.CollectionOf(entityType));

    private bool IsReplicable(string collection)
    {
        var probe = ResourcePattern.Parse($"{ModuleGrants.ReplicateVerb}/{collection}");
        return securityConfiguration.GetConfiguration().Rights
            .Any(r => !r.IsDenied && !string.IsNullOrEmpty(r.Resource) && ResourcePattern.Parse(r.Resource) == probe);
    }

    /// <summary>Write-back applies are saves like any other: their new state is pushed to every replica, the requester's too.</summary>
    public bool HandlesSync => true;

    public Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
        => fanOut.PublishAsync(change.EntityType, [change.Id], cancellationToken);

    public Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
        => fanOut.PublishAsync(change.EntityType, [change.Id], cancellationToken);
}

/// <summary>Turns changed source documents into one push message per HTTP consumer of their collection.</summary>
internal interface IReplicaFanOut
{
    /// <param name="entityTypeName">The source document's CLR type (full name).</param>
    Task PublishAsync(string entityTypeName, IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    /// <summary>The same, for a source collection, bypassing the dirty set (the flush and the reconcile).</summary>
    /// <param name="heldIds">Ids the consumer is known to hold (the reconcile); see <see cref="ReplicaPushMessage.HeldIds"/>.</param>
    Task PublishCollectionAsync(string sourceCollection, IReadOnlyCollection<string> ids, CancellationToken cancellationToken, string? onlyConsumer = null, string? onlyTarget = null, IReadOnlyCollection<string>? heldIds = null);
}

internal sealed partial class ReplicaFanOut : IReplicaFanOut
{
    [Inject] private readonly IReplicaSubscriptions subscriptions;
    [Inject] private readonly IReplicaSourceTypes sourceTypes;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;

    public async Task PublishAsync(string entityTypeName, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (FindType(entityTypeName) is not { } type)
            return;

        var collection = sourceTypes.CollectionOf(type);
        if (options.Value.DirtySetCollections.Contains(collection))
        {
            if ((await subscriptions.DeliveringAsync(collection, cancellationToken)).Any(IsHttp))
                await MarkDirtyAsync(collection, ids, cancellationToken);
            return;
        }

        await PublishCollectionAsync(collection, ids, cancellationToken);
    }

    public async Task PublishCollectionAsync(string sourceCollection, IReadOnlyCollection<string> ids, CancellationToken cancellationToken, string? onlyConsumer = null, string? onlyTarget = null, IReadOnlyCollection<string>? heldIds = null)
    {
        if (ids.Count == 0)
            return;

        var push = options.Value.Push;
        var ladder = push.ResolvedRetryLadder;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var held = heldIds is null ? null : new HashSet<string>(heldIds, StringComparer.OrdinalIgnoreCase);
        foreach (var subscription in await subscriptions.DeliveringAsync(sourceCollection, cancellationToken))
        {
            if (!IsHttp(subscription)
                || (onlyConsumer is not null && !string.Equals(subscription.ConsumerModule, onlyConsumer, StringComparison.OrdinalIgnoreCase))
                || (onlyTarget is not null && subscription.TargetCollection != onlyTarget))
                continue;

            foreach (var batch in ids.Chunk(Math.Max(1, push.BatchSize)))
            {
                await messageBus.BroadcastAsync(new ReplicaPushMessage
                {
                    Consumer = subscription.ConsumerModule,
                    TargetCollection = subscription.TargetCollection,
                    Ids = [.. batch],
                    HeldIds = held is null ? [] : [.. batch.Where(held.Contains)],
                    GiveUpAtUtc = now + push.GiveUpAfter,
                }, new BroadcastOptions
                {
                    MaxAttempts = RetryLadder.Budget(ladder, push.GiveUpAfter),
                    ExpiresAtUtc = now + push.GiveUpAfter + TimeSpan.FromHours(1),
                }, cancellationToken);
            }
        }
    }

    private static bool IsHttp(SparkReplicationSubscription subscription) => subscription.Deployed?.Transport == ReplicationTransport.Http;

    /// <summary>
    /// A high-churn collection: the change only marks the documents dirty (one marker per document, overwritten by every
    /// further change), and <c>ReplicaDirtySetFlushJob</c> pushes their latest state once.
    /// </summary>
    private async Task MarkDirtyAsync(string collection, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var id in ids)
        {
            var markerId = ReplicaDirtyMarker.For(collection, id);
            await session.StoreAsync(new ReplicaDirtyMarker { Id = markerId, SourceCollection = collection, DocumentId = id, MarkedAtUtc = now }, markerId, cancellationToken);
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    internal static Type? FindType(string fullName)
        => Type.GetType(fullName, throwOnError: false)
            ?? AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)
                .Select(a => a.GetType(fullName, throwOnError: false)).FirstOrDefault(t => t is not null);
}

/// <summary>
/// <see cref="IReplicaPublisher"/>: records a raw write in the write's own session as the same durable work a pipeline
/// save produces, so it is pushed after the commit — or not at all.
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
