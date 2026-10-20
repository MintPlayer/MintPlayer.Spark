using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Consumer side of write-back (warehouse §4.2.5, owner decisions 2026-10-20). A save of a replica is never written
/// locally: the replica changes only when the owner's delivery arrives.
/// <list type="bullet">
/// <item><b>Synchronous first.</b> The save calls the owner's <c>/spark/sync/apply</c> over the mTLS channel within the
/// request (<c>Spark:Replication:WriteBack:SynchronousTimeout</c>, 5 s). The owner's answer is the save's: applied →
/// "saved at {owner}" (202, kind <c>replication</c>); a conflict → the 409 a person gets, naming the properties;
/// forbidden or invalid → the validation answer.</item>
/// <item><b>Queued only when the owner does not answer</b> (unreachable, 5xx, timeout): the <see cref="SparkSyncAction"/>
/// and its outbox message are stored in the save's own commit (F13), and the answer is "saved, pending at {owner}"
/// (202, kind <c>replication-pending</c>).</item>
/// <item><b>Per-property compare-and-set:</b> every changed property (<c>IsValueChanged</c>) carries the replica's value
/// when the edit started as its base; unlisted properties never conflict.</item>
/// <item><b>One pending change per row</b> (Q-W28): a second edit while one is pending is refused locally.</item>
/// <item>A replica that is not <c>WriteBack</c> is read-only; a replica is never created here.</item>
/// </list>
/// The row shows a pending badge while a change waits (<c>metadata.replication</c>, <see cref="OnAfterLoadAsync"/>).
/// </summary>
internal sealed partial class ReplicaWriteBackInterceptor : ISaveReplacement, IDeleteReplacement, IAfterLoad
{
    /// <summary>The <c>PersistentObject.Metadata</c> key of the pending badge.</summary>
    public const string MetadataKey = "replication";

    [Inject] private readonly ReplicaCatalog catalog;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly ISyncActionSender sender;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<ReplicaWriteBackInterceptor> logger;
    [Inject] private readonly ISparkCurrentUser? currentUser = null;

    public bool AppliesTo(Type entityType) => catalog.IsReplica(entityType);

    public async ValueTask<SaveReplacementReceipt?> ReplaceAsync(SaveReplacementContext context)
    {
        var descriptor = Describe(context.EntityType);
        if (context.IsNew)
            throw new SparkValidationException($"{descriptor.Type.Name} is a copy of another module's data: create it there.");

        var session = (IAsyncDocumentSession)context.Session;
        var id = context.Id!;
        var replica = await ReadReplicaAsync(descriptor, id);
        var owner = OwnerOf(descriptor, replica);
        await EnsureNotPendingAsync(session, id, replica.ChangeVector, owner);

        var changed = context.PersistentObject.Attributes.Where(a => a.IsValueChanged).Select(a => a.Name).ToList();
        if (changed.Count == 0)
            throw new SparkCancelException();

        var nested = changed.Where(name => !descriptor.WritableFields.ContainsKey(name)).ToList();
        if (nested.Count > 0)
            throw new SparkValidationException($"{string.Join(", ", nested)} cannot be changed here: {owner} computes it.", nested[0]);

        // The base is what the person started from: the replica as stored. The new values come from the posted object,
        // through the same mapping a local save uses, and both are JSON in the store's own serialization.
        var before = ReplicaJson.Serialize(documentStore, context.Before ?? replica.Entity);
        var after = ReplicaJson.Serialize(documentStore, entityMapper.ToEntity(context.PersistentObject));
        var changes = changed.Select(name => new SyncPropertyChange
        {
            Property = descriptor.WritableFields[name],
            Base = Property(before, name),
            Value = Property(after, name),
        }).ToList();

        return await SendOrQueueAsync(session, descriptor, owner, id, replica.ChangeVector, SyncActionType.Update, changes, isDelete: false);
    }

    public async ValueTask<bool> ReplaceAsync(DeleteContext context)
    {
        var descriptor = Describe(context.EntityType);
        var session = (IAsyncDocumentSession)context.Session;
        var replica = await ReadReplicaAsync(descriptor, context.Id);
        var owner = OwnerOf(descriptor, replica);
        await EnsureNotPendingAsync(session, context.Id, replica.ChangeVector, owner);

        // A delete compares every writable field: a row changed at the owner since it was seen is a conflict.
        var before = ReplicaJson.Serialize(documentStore, context.Entity);
        var changes = descriptor.WritableFields.Select(f => new SyncPropertyChange
        {
            Property = f.Value,
            Base = Property(before, f.Key),
            Value = SyncChanges.Null,
        }).ToList();

        await SendOrQueueAsync(session, descriptor, owner, context.Id, replica.ChangeVector, SyncActionType.Delete, changes, isDelete: true);
        // The delete becomes a save of the replica as it is: it disappears when the owner's tombstone arrives.
        return true;
    }

    /// <summary><c>metadata.replication = { pending, owner, syncActionId, own }</c> while a change of the row waits.</summary>
    public async ValueTask OnAfterLoadAsync(LoadContext context)
    {
        var po = context.PersistentObject;
        if (po.Id is not { Length: > 0 } id || catalog.For(context.EntityType) is not { WriteBack: true })
            return;

        var session = context.GetSession();
        var marker = await session.LoadAsync<SyncPendingMarker>(SyncPendingMarker.For(id));
        if (marker is null || !SyncPending.IsPending(marker, po.Etag, timeProvider.GetUtcNow().UtcDateTime))
            return;

        po.Metadata ??= [];
        po.Metadata[MetadataKey] = new Dictionary<string, object?>
        {
            ["pending"] = true,
            ["owner"] = marker.OwnerModule,
            ["syncActionId"] = marker.SyncActionId,
            ["own"] = marker.InitiatorId is not null && marker.InitiatorId == currentUser?.Id,
        };
    }

    private async Task<SaveReplacementReceipt> SendOrQueueAsync(IAsyncDocumentSession session, ReplicaDescriptor descriptor, string owner,
        string id, string? replicaChangeVector, SyncActionType actionType, List<SyncPropertyChange> changes, bool isDelete)
    {
        var settings = options.Value.WriteBack;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var initiator = currentUser is { IsAuthenticated: true } ? currentUser.Id : null;
        var request = new SyncApplyRequest
        {
            RequestingModule = options.Value.ModuleName,
            OpId = Guid.NewGuid().ToString("N"),
            ActionType = actionType,
            SourceCollection = descriptor.SourceCollection,
            DocumentId = id,
            Changes = changes,
            InitiatorId = initiator,
        };

        var answer = await sender.SendAsync(owner, request, settings.SynchronousTimeout, CancellationToken.None);
        if (answer is { IsSuccess: true, Value.Outcome: SyncApplyOutcome.Applied })
        {
            await StoreMarkerAsync(session, new SyncPendingMarker
            {
                DocumentId = id,
                OwnerModule = owner,
                ReplicaChangeVector = isDelete ? null : replicaChangeVector,
                InitiatorId = initiator,
                CreatedAtUtc = now,
            }, now);
            logger.LogInformation("Write-back {OpId} of {Collection}/{Id} applied at '{Owner}' within the save.", request.OpId, descriptor.TargetCollection, id, owner);
            return new SaveReplacementReceipt("replication", id);
        }

        if (!answer.IsTransient)
            throw Refusal(descriptor, owner, answer.Value, answer.Body);

        // The owner did not answer: queue it in this commit.
        var syncActionId = SparkSyncAction.For(request.OpId);
        await session.StoreAsync(new SparkSyncAction
        {
            Id = syncActionId,
            OpId = request.OpId,
            OwnerModule = owner,
            ActionType = actionType,
            SourceCollection = descriptor.SourceCollection,
            TargetCollection = descriptor.TargetCollection,
            DocumentId = id,
            Changes = [.. changes.Select(SyncChanges.ToStored)],
            Summary = isDelete ? "Delete" : string.Join(", ", changes.Select(c => c.Property)),
            InitiatorId = initiator,
            CreatedAtUtc = now,
            GiveUpAtUtc = now + settings.GiveUpAfter,
            Reason = answer.Body,
        }, syncActionId);
        await StoreMarkerAsync(session, new SyncPendingMarker
        {
            DocumentId = id,
            OwnerModule = owner,
            SyncActionId = syncActionId,
            ReplicaChangeVector = isDelete ? null : replicaChangeVector,
            InitiatorId = initiator,
            CreatedAtUtc = now,
        }, now);
        await outbox.EnqueueAsync(session, new SyncActionDispatchMessage { SyncActionId = syncActionId }, new BroadcastOptions
        {
            MaxAttempts = RetryLadder.Budget(settings.ResolvedRetryLadder, settings.GiveUpAfter),
            ExpiresAtUtc = now + settings.GiveUpAfter + TimeSpan.FromHours(1),
        });
        logger.LogInformation("Write-back {OpId} of {Collection}/{Id} queued for '{Owner}': {Reason}", request.OpId, descriptor.TargetCollection, id, owner, answer.Body);
        return new SaveReplacementReceipt("replication-pending", syncActionId);
    }

    /// <summary>The owner's settled refusal as the answer a local save would give.</summary>
    internal static Exception Refusal(ReplicaDescriptor descriptor, string owner, SyncApplyResult? result, string? body)
    {
        var attributes = (result?.Attributes ?? []).Select(a => ReplicaName(descriptor, a)).ToList();
        if (result?.Outcome == SyncApplyOutcome.Conflict)
        {
            var reason = result.Reason == SparkConflictException.Deleted ? SparkConflictException.Deleted : SparkConflictException.Changed;
            var message = reason == SparkConflictException.Deleted
                ? $"This row was deleted at {owner}."
                : $"This row changed at {owner} since you opened it{(attributes.Count > 0 ? $" ({string.Join(", ", attributes)})" : "")}; reload and edit again.";
            return new SparkConflictException(reason, message, attributes);
        }

        var text = result?.Refusal switch
        {
            SyncRefusalKind.Forbidden => $"{owner} does not allow this change{(attributes.Count > 0 ? $" ({string.Join(", ", attributes)})" : "")}.",
            SyncRefusalKind.Invalid => $"{owner} refused the change: {result.Reason}",
            _ => $"{owner} refused the change: {result?.Reason ?? body ?? "no reason given"}",
        };
        return new SparkValidationException(text, attributes.FirstOrDefault());
    }

    /// <summary>The replica's name for an owner property, for messages on the replica's form.</summary>
    private static string ReplicaName(ReplicaDescriptor descriptor, string ownerProperty)
        => descriptor.WritableFields.FirstOrDefault(f => string.Equals(f.Value, ownerProperty, StringComparison.OrdinalIgnoreCase)).Key ?? ownerProperty;

    private ReplicaDescriptor Describe(Type entityType)
    {
        var descriptor = catalog.For(entityType) ?? throw new InvalidOperationException($"{entityType.Name} is not a replica.");
        if (!descriptor.WriteBack)
            throw new SparkValidationException($"{entityType.Name} is a read-only copy of {string.Join("/", descriptor.Sources)}'s data.");
        return descriptor;
    }

    /// <summary>
    /// Q-W28: one pending change per row. The second edit would carry the same, soon stale, base, so it is refused here
    /// instead of conflicting at the owner.
    /// </summary>
    private async Task EnsureNotPendingAsync(IAsyncDocumentSession session, string id, string? replicaChangeVector, string owner)
    {
        var marker = await session.LoadAsync<SyncPendingMarker>(SyncPendingMarker.For(id));
        if (SyncPending.IsPending(marker, replicaChangeVector, timeProvider.GetUtcNow().UtcDateTime))
            throw new SparkValidationException($"A change to this row is still pending at {marker!.OwnerModule ?? owner}.");
    }

    private async Task StoreMarkerAsync(IAsyncDocumentSession session, SyncPendingMarker marker, DateTime now)
    {
        var markerId = SyncPendingMarker.For(marker.DocumentId);
        if (await session.LoadAsync<SyncPendingMarker>(markerId) is { } existing)
            session.Delete(existing);
        marker.Id = markerId;
        await session.StoreAsync(marker, markerId);
        if (marker.SyncActionId is null)
            session.Advanced.GetMetadataFor(marker)[Raven.Client.Constants.Documents.Metadata.Expires] = (now + SyncPendingMarker.AppliedLifetime).ToString("O");
    }

    /// <summary>The stored replica with its own and its source's metadata, from a session of its own.</summary>
    private async Task<(object Entity, string? ChangeVector, string? SourceModule)> ReadReplicaAsync(ReplicaDescriptor descriptor, string id)
    {
        using var session = documentStore.OpenAsyncSession();
        var entity = await ReplicaJson.LoadAsync(session, descriptor.Type, id, CancellationToken.None)
            ?? throw new SparkConflictException(SparkConflictException.Deleted, "This row no longer exists.");
        var metadata = session.Advanced.GetMetadataFor(entity);
        var source = metadata.TryGetValue(ReplicaMetadata.SourceModule, out var value) ? value?.ToString() : null;
        return (entity, session.Advanced.GetChangeVectorFor(entity), source);
    }

    private static string OwnerOf(ReplicaDescriptor descriptor, (object Entity, string? ChangeVector, string? SourceModule) replica)
        => replica.SourceModule is { Length: > 0 } stamped ? stamped
            : descriptor.Sources.Count == 1 ? descriptor.Sources[0]
            : throw new SparkValidationException("This row does not say which module owns it; it cannot be changed here.");

    private static JsonElement Property(JsonElement json, string name)
        => json.TryGetProperty(name, out var value) ? value.Clone() : SyncChanges.Null;
}

/// <summary>
/// Consumer: sends a queued write-back (§4.2.5). Applied → Completed (the badge stays until the delivery rewrites the
/// replica); a conflict or a refusal → Conflict/Refused, the pending mark removed, the initiator told; no answer → the
/// ladder (1 m / 5 m / 30 m / 2 h) until <see cref="SparkSyncAction.GiveUpAtUtc"/> (48 h), then GaveUp: the initiator
/// told and mailed. Today's worker recorded any 2xx as Completed and gave up after about 5–8 minutes (F12, F14).
/// </summary>
internal sealed partial class SyncActionDispatchRecipient : IRecipient<SyncActionDispatchMessage>
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ISyncActionSender sender;
    [Inject] private readonly ISyncOutcomeNotifier notifier;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<SyncActionDispatchRecipient> logger;

    public async Task HandleAsync(SyncActionDispatchMessage message, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        var action = await session.LoadAsync<SparkSyncAction>(message.SyncActionId, cancellationToken);
        if (action is null || action.Status != SyncActionStatuses.Pending)
            return;

        var answer = await sender.SendAsync(action.OwnerModule, new SyncApplyRequest
        {
            RequestingModule = options.Value.ModuleName,
            OpId = action.OpId,
            ActionType = action.ActionType,
            SourceCollection = action.SourceCollection,
            DocumentId = action.DocumentId,
            Changes = [.. action.Changes.Select(SyncChanges.ToWire)],
            InitiatorId = action.InitiatorId,
        }, timeout: null, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        action.Attempts++;
        var marker = await session.LoadAsync<SyncPendingMarker>(SyncPendingMarker.For(action.DocumentId), cancellationToken);
        var ownsMarker = marker?.SyncActionId == action.Id;

        if (answer is { IsSuccess: true, Value.Outcome: SyncApplyOutcome.Applied })
        {
            action.Status = SyncActionStatuses.Completed;
            action.Reason = null;
            action.CompletedAtUtc = now;
            if (ownsMarker)
            {
                // Applied: pending until the owner's delivery rewrites the replica.
                marker!.SyncActionId = null;
                marker.CreatedAtUtc = now;
                session.Advanced.GetMetadataFor(marker)[Raven.Client.Constants.Documents.Metadata.Expires] = (now + SyncPendingMarker.AppliedLifetime).ToString("O");
            }
            await session.SaveChangesAsync(cancellationToken);
            return;
        }

        if (answer.IsTransient && now < action.GiveUpAtUtc)
        {
            action.Reason = answer.Body ?? answer.Error?.Message;
            await session.SaveChangesAsync(cancellationToken);
            var settings = options.Value.WriteBack;
            throw new RetryAfterException(RetryLadder.Next(settings.ResolvedRetryLadder, now - action.CreatedAtUtc),
                $"Write-back {action.OpId} to '{action.OwnerModule}' deferred: {action.Reason}");
        }

        if (answer.IsTransient)
        {
            action.Status = SyncActionStatuses.GaveUp;
            action.Reason = $"{action.OwnerModule} could not be reached for {options.Value.WriteBack.GiveUpAfter.TotalHours:0} hours ({answer.Body ?? answer.Error?.Message}).";
        }
        else
        {
            action.Status = answer.Value?.Outcome == SyncApplyOutcome.Conflict ? SyncActionStatuses.Conflict : SyncActionStatuses.Refused;
            action.Reason = answer.Value?.Reason ?? answer.Body;
            action.Attributes = answer.Value?.Attributes ?? [];
        }
        action.CompletedAtUtc = now;
        if (ownsMarker)
            session.Delete(marker!);
        await session.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Write-back {OpId} of {Collection}/{Id} ended {Status} at '{Owner}': {Reason}",
            action.OpId, action.TargetCollection, action.DocumentId, action.Status, action.OwnerModule, action.Reason);
        await notifier.NotifyAsync(action, cancellationToken);
    }
}

/// <summary>Consumer: delivers a replica change to every <see cref="IReplicaChanged{T}"/> of its replica type.</summary>
internal sealed partial class ReplicaChangedRecipient : IRecipient<ReplicaChangedMessage>
{
    [Inject] private readonly ReplicaCatalog catalog;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IServiceProvider services;

    public async Task HandleAsync(ReplicaChangedMessage message, CancellationToken cancellationToken = default)
    {
        if (catalog.ForTarget(message.TargetCollection) is not { } descriptor)
            return;

        var deliver = typeof(ReplicaChangedRecipient).GetMethod(nameof(DeliverAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(descriptor.Type);
        await (Task)deliver.Invoke(this, [message, cancellationToken])!;
    }

    private async Task DeliverAsync<T>(ReplicaChangedMessage message, CancellationToken cancellationToken) where T : class
    {
        var previous = message.PreviousJson is null ? null : await ReadAsync<T>(message.PreviousJson, message.Id, cancellationToken);
        var current = message.CurrentJson is null ? null : await ReadAsync<T>(message.CurrentJson, message.Id, cancellationToken);
        var change = new ReplicaChange<T>(message.Id, message.SourceModule, previous, current, message.SourceChangeVector);
        foreach (var observer in services.GetServices<IReplicaChanged<T>>())
            await observer.OnReplicaChangedAsync(change, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(string json, string id, CancellationToken cancellationToken) where T : class
        => (T)await ReplicaJson.DeserializeAsync(documentStore, typeof(T), SyncChanges.Parse(json), id, cancellationToken);
}
