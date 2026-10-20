using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Owner, HTTP transport (warehouse §4.2.4): repairs what pushes missed — a raw write nobody marked, a push that
/// dead-lettered, a restore. For one subscription it reads the consumer's digest (its replicas' source change vectors)
/// and streams this module's source collection: missing or older at the consumer → pushed, present at the consumer
/// but gone here → pushed as a tombstone. Pushes reuse delivery unchanged, so the reconcile adds no second write path.
/// The grant and read rights are checked again first; a loss suspends (§4.2.6).
/// <para>
/// The digest is held in memory (id → change vector) rather than merge-joined: the two sides need not agree on how
/// RavenDB orders ids, and a replica collection fits comfortably.
/// </para>
/// </summary>
internal interface IReplicaReconciler
{
    /// <returns>The number of documents pushed.</returns>
    Task<int> ReconcileAsync(string consumer, string targetCollection, CancellationToken cancellationToken);
}

internal sealed partial class ReplicaReconciler : IReplicaReconciler
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IModuleDirectory moduleDirectory;
    [Inject] private readonly IReplicationHttpClientProvider httpClients;
    [Inject] private readonly IModuleGrants grants;
    [Inject] private readonly IReplicaSubscriptions subscriptions;
    [Inject] private readonly IReplicaFanOut fanOut;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly ILogger<ReplicaReconciler> logger;

    public async Task<int> ReconcileAsync(string consumer, string targetCollection, CancellationToken cancellationToken)
    {
        SparkReplicationSubscription? subscription;
        using (var session = documentStore.OpenAsyncSession())
            subscription = await session.LoadAsync<SparkReplicationSubscription>(SparkReplicationSubscription.For(consumer, targetCollection), cancellationToken);

        if (subscription is not { IsDelivering: true, Deployed.Transport: ReplicationTransport.Http }
            || !ReplicaScriptRenderer.TryParse(subscription.ApprovedManifest, out var manifest, out _))
            return 0;

        var grant = await grants.CheckAsync(consumer, subscription.SourceCollection, manifest!, cancellationToken);
        if (!grant.Allowed)
        {
            await subscriptions.SuspendAsync(consumer, targetCollection, grant.Refusal!, cancellationToken);
            return 0;
        }

        var registration = await moduleDirectory.FindAsync(consumer, cancellationToken);
        if (registration is null || string.IsNullOrEmpty(registration.AppUrl))
            throw new InvalidOperationException($"Module '{consumer}' is not registered in SparkModules; the reconcile retries.");

        var held = await ReadDigestAsync(consumer, registration.AppUrl, targetCollection, cancellationToken);

        var toPush = new List<string>();
        using (var session = documentStore.OpenAsyncSession())
        {
            // A collection query: the name is the subscription's, validated as an identifier at registration, and never RQL text.
            var query = session.Advanced.AsyncDocumentQuery<object>(collectionName: subscription.SourceCollection);
            await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
            {
                var current = stream.Current;
                if (!held.Remove(current.Id, out var consumerVector)
                    || SparkChangeVector.Compare(current.ChangeVector, consumerVector) is SparkChangeVectorOrder.Newer or SparkChangeVectorOrder.Concurrent)
                    toPush.Add(current.Id);
            }
        }

        // Present at the consumer, gone here: the push finds nothing and sends a tombstone.
        toPush.AddRange(held.Keys);

        await fanOut.PublishCollectionAsync(subscription.SourceCollection, toPush, cancellationToken, onlyConsumer: consumer, onlyTarget: targetCollection);
        logger.LogInformation("Reconciled '{Target}' of '{Consumer}': {Count} document(s) pushed.", targetCollection, consumer, toPush.Count);
        return toPush.Count;
    }

    private async Task<Dictionary<string, string?>> ReadDigestAsync(string consumer, string appUrl, string targetCollection, CancellationToken cancellationToken)
    {
        var held = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var client = httpClients.GetClient(consumer);
        var pageSize = Math.Max(1, options.Value.Reconcile.PageSize);
        string? after = null;
        do
        {
            var path = $"/spark/replicas/digest?module={Uri.EscapeDataString(options.Value.ModuleName)}&collection={Uri.EscapeDataString(targetCollection)}&take={pageSize}"
                + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");
            var result = await ModuleCalls.GetAsync<ReplicaDigestPage>(client, appUrl, path, cancellationToken);
            if (!result.IsSuccess || result.Value is null)
                throw new InvalidOperationException($"The digest of '{targetCollection}' at '{consumer}' could not be read ({(result.StatusCode is { } code ? (int)code : 0)}).");

            foreach (var entry in result.Value.Items)
                held[entry.Id] = entry.SourceChangeVector;
            after = result.Value.Next;
        }
        while (after is not null);
        return held;
    }
}

/// <summary>Owner: the initial load after an approval (queued by the deploy).</summary>
internal sealed partial class ReplicaReconcileRecipient : IRecipient<ReplicaReconcileMessage>
{
    [Inject] private readonly IReplicaReconciler reconciler;

    public Task HandleAsync(ReplicaReconcileMessage message, CancellationToken cancellationToken = default)
        => reconciler.ReconcileAsync(message.Consumer, message.TargetCollection, cancellationToken);
}

/// <summary>
/// Owner: the hourly reconcile of every HTTP subscription (<c>Spark:Replication:Reconcile:Schedule</c>), one run per
/// occurrence across the cluster (the cron scheduler's compare-exchange claim). One failing consumer does not stop
/// the others.
/// </summary>
internal sealed partial class ReplicaReconcileJob : ISparkCronJob
{
    public static string CronSchedule => "0 * * * *";

    [Inject] private readonly IReplicaSubscriptions subscriptions;
    [Inject] private readonly IReplicaReconciler reconciler;
    [Inject] private readonly ILogger<ReplicaReconcileJob> logger;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in await subscriptions.AllDeliveringAsync(cancellationToken))
        {
            if (subscription.Deployed?.Transport != ReplicationTransport.Http)
                continue;
            try
            {
                await reconciler.ReconcileAsync(subscription.ConsumerModule, subscription.TargetCollection, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The reconcile of '{Target}' to '{Consumer}' failed; the next run tries again.", subscription.TargetCollection, subscription.ConsumerModule);
            }
        }
    }
}

/// <summary>
/// Owner: pushes the latest state of every document marked dirty (<c>Spark:Replication:DirtySetCollections</c>), every
/// <c>Spark:Replication:Push:DirtySetFlushSchedule</c>. A marker is removed with optimistic concurrency, so one a newer
/// change rewrote meanwhile stays for the next flush.
/// </summary>
internal sealed partial class ReplicaDirtySetFlushJob : ISparkCronJob
{
    public static string CronSchedule => "*/2 * * * * *";

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IReplicaFanOut fanOut;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var markers = (await session.Advanced.LoadStartingWithAsync<ReplicaDirtyMarker>(ReplicaDirtyMarker.Prefix, pageSize: 1024, token: cancellationToken)).ToList();
        if (markers.Count == 0)
            return;

        foreach (var group in markers.GroupBy(m => m.SourceCollection, StringComparer.Ordinal))
            await fanOut.PublishCollectionAsync(group.Key, [.. group.Select(m => m.DocumentId).Distinct(StringComparer.OrdinalIgnoreCase)], cancellationToken);

        foreach (var marker in markers)
            session.Delete(marker);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            // A marker changed after it was read: its document is pushed again next time, which is harmless.
        }
    }
}
