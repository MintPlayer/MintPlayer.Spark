using System.Security.Cryptography;
using System.Text;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Apps.Services;

/// <summary>One <see cref="AppsBuilder.AddEntityEvents{T}"/> call.</summary>
internal sealed record EntityEventRegistration(Type EntityType, EntityEvents Events, Func<object, WebhookOwner?> Owner, Type InterceptorType);

/// <summary>The <see cref="AppsBuilder.AddEntityEvents{T}"/> registrations by entity type.</summary>
internal sealed class EntityEventRegistrations
{
    private readonly Dictionary<Type, EntityEventRegistration> byType;

    public EntityEventRegistrations(IEnumerable<EntityEventRegistration> registrations)
    {
        byType = [];
        foreach (var registration in registrations)
        {
            if (!byType.TryAdd(registration.EntityType, registration))
                throw new InvalidOperationException($"AddEntityEvents<{registration.EntityType.Name}>() is called twice; name every event in one call.");
        }
    }

    public EntityEventRegistration For(Type entityType) => byType[entityType];
}

/// <summary>
/// The entity lifecycle events (D31). The before-interceptors record the row's owner in the write's facts, while the
/// entity is at hand (a deleted row cannot be read afterwards). The durable after-commit interceptors publish the event:
/// only once the write committed, at least once, under a <c>webhook-id</c> derived from the write, so a retried run
/// delivers nothing twice.
/// </summary>
internal sealed partial class EntityWebhookInterceptor<T> : IBeforeSave<T>, IBeforeDelete<T>, IAfterSaveCommitted<T>, IAfterDeleteCommitted<T>
    where T : class
{
    internal const string OwnerKindFact = "Spark.Apps.OwnerKind";
    internal const string OwnerKeyFact = "Spark.Apps.OwnerKey";

    [Inject] private readonly EntityEventRegistrations registrations;
    [Inject] private readonly ISparkStore store;
    [Inject] private readonly WebhookPublisher publisher;

    private EntityEventRegistration Registration => registrations.For(typeof(T));

    // Finalize: the owner as the other interceptors left the row.
    InterceptorStage IBeforeSave.Stage => InterceptorStage.Finalize;

    InterceptorStage IBeforeDelete.Stage => InterceptorStage.Finalize;

    public ValueTask OnBeforeSaveAsync(T entity, SaveContext context)
    {
        RecordOwner(entity, context);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnBeforeDeleteAsync(T entity, DeleteContext context)
    {
        RecordOwner(entity, context);
        return ValueTask.CompletedTask;
    }

    private void RecordOwner(T entity, SparkInterceptorContext context)
    {
        if (Registration.Owner(entity) is not { } owner)
            return;
        context.Facts[OwnerKindFact] = owner.Kind;
        context.Facts[OwnerKeyFact] = owner.Key;
    }

    public Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        if (change.IsNew)
            return Registration.Events.HasFlag(EntityEvents.Created) ? PublishAsync(change, new EntityCreated<T>(change.Id), cancellationToken) : Task.CompletedTask;
        if (!Registration.Events.HasFlag(EntityEvents.Updated))
            return Task.CompletedTask;
        var changed = change.Facts.TryGetValue(SparkFacts.ChangedAttributes, out var list)
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return PublishAsync(change, new EntityUpdated<T>(change.Id, changed), cancellationToken);
    }

    public Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
        => Registration.Events.HasFlag(EntityEvents.Deleted)
            ? PublishAsync(change, new EntityDeleted<T>(change.Id, change.IsReplaced), cancellationToken)
            : Task.CompletedTask;

    private async Task PublishAsync(SparkCommittedChange change, object data, CancellationToken cancellationToken)
    {
        if (!change.Facts.TryGetValue(OwnerKindFact, out var kind) || !change.Facts.TryGetValue(OwnerKeyFact, out var key))
            return;
        using var session = store.OpenSession();
        await publisher.PublishAsync(data, new WebhookOwner(kind, key), session, MessageIdFor(change, data), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A <c>msg_</c> id like <see cref="WebhookPublisher.NewMessageId"/>, but derived from the write instead of random.</summary>
    internal static string MessageIdFor(SparkCommittedChange change, object data)
    {
        var key = Encoding.UTF8.GetBytes($"{data.GetType().FullName}|{change.Id.Length}:{change.Id}|{change.OccurredAt.UtcTicks}");
        return "msg_" + change.OccurredAt.ToUnixTimeMilliseconds().ToString("x12") + Convert.ToHexStringLower(SHA256.HashData(key).AsSpan(0, 10));
    }
}
