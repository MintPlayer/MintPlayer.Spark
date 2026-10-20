using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Warehouse.Indexes;
using MintPlayer.Spark.Warehouse.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Work;

public enum TaskClaimOutcome
{
    Claimed,
    /// <summary>The caller already held it; the lease was renewed.</summary>
    Renewed,
    /// <summary>Someone else holds a live lease.</summary>
    HeldByOther,
    NotOpen,
    Unknown,
}

public enum TaskConfirmOutcome
{
    Confirmed,
    /// <summary>The op id was applied before (WI4): nothing changed.</summary>
    Replayed,
    /// <summary>The caller does not hold the task's lease.</summary>
    NotHolder,
    NotOpen,
    Unknown,
}

/// <summary>
/// The task envelope (PRD §4.5, WD5): claim with a lease (commerce X12 semantics on the task document, under optimistic
/// concurrency: two pickers claiming at once conflict, one wins), confirm with op ids, and a productivity event per finished
/// task (Q-W19). The stock changes a confirmation causes are the caller's (<c>IWarehouseStock</c>, with the same op id).
/// </summary>
public interface IWarehouseTasks
{
    Task<TaskClaimOutcome> ClaimAsync(string taskId, string userId, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(string taskId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Confirms <paramref name="quantity"/> on the task; finishing it records a <see cref="ProductivityEvent"/>.</summary>
    Task<TaskConfirmOutcome> ConfirmAsync(string taskId, string userId, string opId, decimal quantity, bool finish, CancellationToken cancellationToken = default);

    /// <summary>The open and assigned tasks of a site, in walk-sequence order (Warehouse/Work), owner-filtered (WI5).</summary>
    Task<IReadOnlyList<WarehouseTask>> QueueAsync(string siteId, IReadOnlyCollection<string>? visibleOwners, string? userId = null, CancellationToken cancellationToken = default);
}

internal sealed partial class WarehouseTasks : IWarehouseTasks
{
    private const int RememberedOps = 64;

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkWarehouseOptions> options;
    [Inject] private readonly TimeProvider timeProvider;

    public async Task<TaskClaimOutcome> ClaimAsync(string taskId, string userId, CancellationToken cancellationToken = default)
    {
        using var session = OpenSession();
        var task = await session.LoadAsync<WarehouseTask>(taskId, cancellationToken);
        if (task is null)
            return TaskClaimOutcome.Unknown;
        if (task.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
            return TaskClaimOutcome.NotOpen;

        var now = timeProvider.GetUtcNow();
        var renewed = task.AssignedTo == userId;
        if (!renewed && task.AssignedTo is not null && task.LeaseUntil > now)
            return TaskClaimOutcome.HeldByOther;

        task.AssignedTo = userId;
        task.LeaseUntil = now + options.Value.Tasks.Lease;
        if (task.State == WarehouseTaskState.Open)
            task.State = WarehouseTaskState.Assigned;
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return TaskClaimOutcome.HeldByOther;
        }
        return renewed ? TaskClaimOutcome.Renewed : TaskClaimOutcome.Claimed;
    }

    public async Task<bool> ReleaseAsync(string taskId, string userId, CancellationToken cancellationToken = default)
    {
        using var session = OpenSession();
        var task = await session.LoadAsync<WarehouseTask>(taskId, cancellationToken);
        if (task is null || task.AssignedTo != userId || task.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
            return false;
        task.AssignedTo = null;
        task.LeaseUntil = null;
        task.State = WarehouseTaskState.Open;
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<TaskConfirmOutcome> ConfirmAsync(string taskId, string userId, string opId, decimal quantity, bool finish, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = OpenSession();
            var task = await session.LoadAsync<WarehouseTask>(taskId, cancellationToken);
            if (task is null)
                return TaskConfirmOutcome.Unknown;
            if (task.ConfirmedOpIds.Contains(opId))
                return TaskConfirmOutcome.Replayed;
            if (task.State is WarehouseTaskState.Done or WarehouseTaskState.Cancelled)
                return TaskConfirmOutcome.NotOpen;
            if (task.AssignedTo != userId)
                return TaskConfirmOutcome.NotHolder;

            var now = timeProvider.GetUtcNow();
            task.Confirmed += quantity;
            task.ConfirmedOpIds.Add(opId);
            if (task.ConfirmedOpIds.Count > RememberedOps)
                task.ConfirmedOpIds.RemoveRange(0, task.ConfirmedOpIds.Count - RememberedOps);
            task.StartedAt ??= now;
            task.State = WarehouseTaskState.InProgress;
            task.LeaseUntil = now + options.Value.Tasks.Lease;
            if (finish)
            {
                task.State = WarehouseTaskState.Done;
                task.CompletedAt = now;
                var eventId = ProductivityEvent.IdPrefix + Uri.EscapeDataString(task.Id!);
                await session.StoreAsync(new ProductivityEvent
                {
                    Id = eventId,
                    Kind = task.Kind,
                    UserId = userId,
                    SiteId = task.SiteId,
                    TaskId = task.Id,
                    Quantity = task.Confirmed,
                    Lines = 1,
                    StartedAt = task.StartedAt ?? now,
                    EndedAt = now,
                }, eventId, cancellationToken);
            }

            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return TaskConfirmOutcome.Confirmed;
            }
            catch (Raven.Client.Exceptions.ConcurrencyException) when (attempt < 5)
            {
            }
        }
    }

    public async Task<IReadOnlyList<WarehouseTask>> QueueAsync(string siteId, IReadOnlyCollection<string>? visibleOwners, string? userId = null, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        var query = session.Query<WarehouseWorkEntry>(WarehouseIndexes.Work)
            .Where(t => t.SiteId == siteId && (t.State == WarehouseTaskState.Open || t.State == WarehouseTaskState.Assigned || t.State == WarehouseTaskState.InProgress));
        if (userId is not null)
            query = query.Where(t => t.AssignedTo == userId);
        if (visibleOwners is not null)
            query = query.Where(t => t.OrganizationIds.Any(o => o.In(visibleOwners)));
        return await query.OrderBy(t => t.WalkSequence).OfType<WarehouseTask>().Take(1024).ToListAsync(cancellationToken);
    }

    private IAsyncDocumentSession OpenSession()
    {
        var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        return session;
    }
}
