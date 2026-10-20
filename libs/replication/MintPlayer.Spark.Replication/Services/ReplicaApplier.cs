using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>Why a push was refused as a whole, with the HTTP status it maps to.</summary>
internal sealed record ReplicaApplyRefusal(int StatusCode, string Error);

/// <summary>
/// Consumer, HTTP transport (warehouse §4.2.3 "Apply"): stores pushed replicas with the change-vector guard. Per item, in
/// one session with optimistic concurrency: <c>Newer</c> than what the replica (or its tombstone) holds → stored with
/// its source metadata, or deleted with a tombstone; <c>Equal</c>/<c>Older</c> → a no-op that still answers success
/// (a resend after a lost answer is harmless); <c>Concurrent</c> → a no-op; an id another source module holds → refused
/// as poison. A durable replica-changed message is enqueued in the same commit.
/// </summary>
internal interface IReplicaApplier
{
    /// <summary>The caller has been authenticated as <paramref name="module"/>.</summary>
    Task<(ReplicaApplyResult? Result, ReplicaApplyRefusal? Refusal)> ApplyAsync(string module, ReplicaApplyRequest request, CancellationToken cancellationToken);

    /// <summary>One page of this module's replicas of <paramref name="targetCollection"/> that came from <paramref name="module"/>, by id.</summary>
    Task<(ReplicaDigestPage? Page, ReplicaApplyRefusal? Refusal)> DigestAsync(string module, string targetCollection, string? after, int take, CancellationToken cancellationToken);
}

internal sealed partial class ReplicaApplier : IReplicaApplier
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ReplicaCatalog catalog;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<ReplicaApplier> logger;

    public async Task<(ReplicaApplyResult? Result, ReplicaApplyRefusal? Refusal)> ApplyAsync(string module, ReplicaApplyRequest request, CancellationToken cancellationToken)
    {
        if (catalog.ForTarget(request.TargetCollection) is not { } descriptor)
            return (null, new ReplicaApplyRefusal(404, $"No replica collection '{request.TargetCollection}'"));
        if (!descriptor.IsSource(module))
            return (null, new ReplicaApplyRefusal(403, "Forbidden"));

        using var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;

        // Q-W29: the shape this build declares, or the one the owner last acknowledged as approved for it.
        if (request.Hash != descriptor.Hash)
        {
            var state = await session.LoadAsync<ReplicaRegistrationState>(ReplicaRegistrationState.For(module), cancellationToken);
            if (state is null || !state.Items.TryGetValue(request.TargetCollection, out var answer) || answer.ApprovedHash != request.Hash)
                return (null, new ReplicaApplyRefusal(409, "manifest-mismatch"));
        }

        var notify = HasObservers(descriptor.Type);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = new ReplicaApplyResult();
        foreach (var item in request.Items)
            result.Items.Add(await ApplyItemAsync(session, descriptor, module, item, notify, now, cancellationToken));

        await session.SaveChangesAsync(cancellationToken);
        return (result, null);
    }

    private async Task<ReplicaApplyItemResult> ApplyItemAsync(IAsyncDocumentSession session, ReplicaDescriptor descriptor, string module,
        ReplicaItem item, bool notify, DateTime now, CancellationToken cancellationToken)
    {
        var existing = await ReplicaJson.LoadAsync(session, descriptor.Type, item.Id, cancellationToken);
        var tombstoneId = ReplicaTombstone.For(item.Id);
        var tombstone = await session.LoadAsync<ReplicaTombstone>(tombstoneId, cancellationToken);

        if (existing is not null && !descriptor.Type.IsInstanceOfType(existing))
            return Outcome(item, ReplicaApplyOutcome.Refused, $"'{item.Id}' is not a {descriptor.Type.Name} here");

        string? heldModule = null;
        string? heldVector = null;
        if (existing is not null)
        {
            var metadata = session.Advanced.GetMetadataFor(existing);
            heldModule = metadata.TryGetValue(ReplicaMetadata.SourceModule, out var m) ? m?.ToString() : null;
            heldVector = metadata.TryGetValue(ReplicaMetadata.SourceChangeVector, out var v) ? v?.ToString() : null;
        }
        else if (tombstone is not null)
        {
            heldModule = tombstone.SourceModule;
            heldVector = tombstone.SourceChangeVector;
        }

        // Ids are unique across the sources of a collection: one held by another source is poison, never overwritten.
        if (heldModule is not null && !string.Equals(heldModule, module, StringComparison.OrdinalIgnoreCase))
            return Outcome(item, ReplicaApplyOutcome.Refused, $"'{item.Id}' is held by source '{heldModule}'");

        // A deletion whose version the owner no longer knows supersedes whatever is held.
        var order = item.Deleted && item.SourceChangeVector is null
            ? SparkChangeVectorOrder.Newer
            : SparkChangeVector.Compare(item.SourceChangeVector, heldVector);
        if (heldVector is null && existing is null && tombstone is null)
            order = SparkChangeVectorOrder.Newer;

        switch (order)
        {
            case SparkChangeVectorOrder.Equal:
            case SparkChangeVectorOrder.Older:
                return Outcome(item, ReplicaApplyOutcome.Stale);
            case SparkChangeVectorOrder.Concurrent:
                logger.LogWarning("Replica {Id} from '{Module}': concurrent change vectors; kept as is until the owner's next state.", item.Id, module);
                return Outcome(item, ReplicaApplyOutcome.Concurrent);
        }

        var previousJson = notify && existing is not null ? ReplicaJson.Serialize(documentStore, existing).GetRawText() : null;

        if (item.Deleted)
        {
            if (existing is not null)
                session.Delete(existing);
            tombstone ??= new ReplicaTombstone { Id = tombstoneId, ReplicaId = item.Id, TargetCollection = descriptor.TargetCollection, SourceModule = module };
            tombstone.SourceChangeVector = item.SourceChangeVector;
            tombstone.DeletedAtUtc = now;
            await session.StoreAsync(tombstone, tombstoneId, cancellationToken);
            session.Advanced.GetMetadataFor(tombstone)[Constants.Documents.Metadata.Expires] = (now + ReplicaTombstone.Lifetime).ToString("O");
            if (existing is not null && notify)
                await EnqueueChangedAsync(session, descriptor, module, item, previousJson, null, cancellationToken);
            return Outcome(item, ReplicaApplyOutcome.Applied);
        }

        if (item.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return Outcome(item, ReplicaApplyOutcome.Refused, "No payload");

        object replica;
        try
        {
            replica = await ReplicaJson.DeserializeAsync(documentStore, descriptor.Type, payload, item.Id, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Outcome(item, ReplicaApplyOutcome.Refused, ex.Message);
        }

        string? expectedVector = null;
        if (existing is not null)
        {
            expectedVector = session.Advanced.GetChangeVectorFor(existing);
            session.Advanced.Evict(existing);
        }
        await session.StoreAsync(replica, expectedVector ?? string.Empty, item.Id, cancellationToken);
        var stamped = session.Advanced.GetMetadataFor(replica);
        stamped[ReplicaMetadata.SourceChangeVector] = item.SourceChangeVector;
        stamped[ReplicaMetadata.SourceModule] = module;
        if (tombstone is not null)
            session.Delete(tombstone);

        if (notify)
            await EnqueueChangedAsync(session, descriptor, module, item, previousJson, payload.GetRawText(), cancellationToken);
        return Outcome(item, ReplicaApplyOutcome.Applied);
    }

    public async Task<(ReplicaDigestPage? Page, ReplicaApplyRefusal? Refusal)> DigestAsync(string module, string targetCollection, string? after, int take, CancellationToken cancellationToken)
    {
        if (catalog.ForTarget(targetCollection) is not { } descriptor)
            return (null, new ReplicaApplyRefusal(404, $"No replica collection '{targetCollection}'"));
        if (!descriptor.IsSource(module))
            return (null, new ReplicaApplyRefusal(403, "Forbidden"));

        take = Math.Clamp(take, 1, 1024);
        using var session = documentStore.OpenAsyncSession();
        // A collection query, never RQL text: the cursor is a parameter, the collection the catalog's own name.
        var query = session.Advanced.AsyncDocumentQuery<object>(collectionName: descriptor.TargetCollection);
        if (after is { Length: > 0 })
            query = query.WhereGreaterThan(Constants.Documents.Indexing.Fields.DocumentIdFieldName, after);
        var page = await query.OrderBy(Constants.Documents.Indexing.Fields.DocumentIdFieldName).Take(take).ToListAsync(cancellationToken);

        var result = new ReplicaDigestPage();
        foreach (var replica in page)
        {
            var metadata = session.Advanced.GetMetadataFor(replica);
            var source = metadata.TryGetValue(ReplicaMetadata.SourceModule, out var m) ? m?.ToString() : null;
            // A replica of another source is not this owner's to reconcile; ETL loads without the stamp count as this owner's.
            if (source is not null && !string.Equals(source, module, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Items.Add(new ReplicaDigestEntry
            {
                Id = session.Advanced.GetDocumentId(replica),
                SourceChangeVector = metadata.TryGetValue(ReplicaMetadata.SourceChangeVector, out var v) ? v?.ToString() : null,
            });
        }
        result.Next = page.Count == take ? session.Advanced.GetDocumentId(page[^1]) : null;
        return (result, null);
    }

    private bool HasObservers(Type replicaType)
        => services.GetService(typeof(IEnumerable<>).MakeGenericType(typeof(IReplicaChanged<>).MakeGenericType(replicaType))) is IEnumerable<object> observers
            && observers.Any();

    private Task EnqueueChangedAsync(IAsyncDocumentSession session, ReplicaDescriptor descriptor, string module, ReplicaItem item,
        string? previousJson, string? currentJson, CancellationToken cancellationToken)
        => outbox.EnqueueAsync(session, new ReplicaChangedMessage
        {
            TargetCollection = descriptor.TargetCollection,
            Id = item.Id,
            SourceModule = module,
            PreviousJson = previousJson,
            CurrentJson = currentJson,
            SourceChangeVector = item.SourceChangeVector,
        }, cancellationToken: cancellationToken);

    private static ReplicaApplyItemResult Outcome(ReplicaItem item, ReplicaApplyOutcome outcome, string? reason = null)
        => new() { Id = item.Id, Outcome = outcome, Reason = reason };
}
