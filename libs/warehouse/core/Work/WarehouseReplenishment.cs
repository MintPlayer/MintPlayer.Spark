using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Stock;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Work;

/// <summary>
/// Min/max replenishment (PRD §4.5, WD5; <c>Spark:Warehouse:Replenishment</c>): a fixed pick face at or below its
/// <c>Min</c> gets one Replenish task up to <c>Max</c>, sourced by FEFO from released stock elsewhere on the site. Runs from
/// a daily sweep and after every short pick (Outbound calls <see cref="OnShortPickAsync"/>). Fixed pick faces only.
/// </summary>
public interface IWarehouseReplenishment
{
    /// <returns>The task created or refreshed, or null when the face needs nothing (or has an open task already).</returns>
    Task<WarehouseTask?> CheckAsync(string pickFaceId, CancellationToken cancellationToken = default);

    Task<WarehouseTask?> OnShortPickAsync(string pickFaceId, CancellationToken cancellationToken = default);

    /// <returns>The number of tasks created.</returns>
    Task<int> SweepAsync(string? siteId = null, CancellationToken cancellationToken = default);
}

internal sealed partial class WarehouseReplenishment : IWarehouseReplenishment
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkWarehouseOptions> options;
    [Inject] private readonly TimeProvider timeProvider;

    /// <summary>One replenishment task per pick face: a second trigger finds it open and does nothing.</summary>
    internal static string TaskIdFor(string pickFaceId) => $"{WarehouseTask.IdPrefix}replenish/{Uri.EscapeDataString(pickFaceId)}";

    public Task<WarehouseTask?> OnShortPickAsync(string pickFaceId, CancellationToken cancellationToken = default)
        => CheckAsync(pickFaceId, cancellationToken);

    public async Task<WarehouseTask?> CheckAsync(string pickFaceId, CancellationToken cancellationToken = default)
    {
        if (options.Value.Replenishment.Mode == ReplenishmentMode.Off)
            return null;

        using var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var face = await session.LoadAsync<WarehouseLocation>(pickFaceId, cancellationToken);
        if (face is not { Active: true, Type: WarehouseLocationType.PickFace, FixedItemId: { } itemId, Min: { } min, Max: { } max } || max <= min)
            return null;

        var taskId = TaskIdFor(face.Id!);
        var task = await session.LoadAsync<WarehouseTask>(taskId, cancellationToken);
        if (task is { State: not (WarehouseTaskState.Done or WarehouseTaskState.Cancelled) })
            return null;

        // The face's own stock: an id-prefix load ({locationId}/{itemId}/…), no index.
        var onFace = (await session.Advanced.LoadStartingWithAsync<StockBalance>($"{face.Id}/{itemId}/", pageSize: 256, token: cancellationToken)).Sum(b => b.OnHand);
        if (onFace > min)
            return null;

        var item = await session.LoadAsync<WarehouseItem>(itemId, cancellationToken);
        if (item is null)
            return null;
        var site = await session.LoadAsync<WarehouseSite>(face.SiteId, cancellationToken);
        var today = DateOnly.FromDateTime(SparkTimeZones.ToZone(timeProvider.GetUtcNow(), SparkTimeZones.FindOrUtc(site?.TimeZone)).DateTime);

        var sources = await StockQueries.ReplenishmentSourcesAsync(session, face.SiteId, itemId, face.Id!, cancellationToken);
        var locations = await session.LoadAsync<WarehouseLocation>(sources.Select(s => s.LocationId).Distinct(), cancellationToken);
        var candidates = sources
            .Where(s => locations.GetValueOrDefault(s.LocationId) is { Active: true, Type: not WarehouseLocationType.PickFace })
            .Select(s => new AllocationCandidate(s.Id!, locations[s.LocationId]!.ToRef(), s.LotCode, s.Expiry, s.ReceivedAt, s.OnHand - s.Allocated))
            .ToList();
        var request = new AllocationRequest(face.SiteId, item.OrganizationId ?? "", taskId, 0, itemId, max - onFace, item.MinimumShelfLifeDays, today);
        var source = AllocationOrder.Fefo(request, candidates).FirstOrDefault();
        if (source is null)
            return null;

        task ??= new WarehouseTask { Id = taskId };
        task.Kind = WarehouseTaskKind.Replenish;
        task.State = WarehouseTaskState.Open;
        task.SiteId = face.SiteId;
        task.LocationId = source.Location.LocationId;
        task.WalkSequence = source.Location.WalkSequence;
        task.ZoneId = source.Location.ZoneId;
        task.TargetLocationId = face.Id;
        task.ItemId = itemId;
        task.LotCode = source.LotCode;
        task.Quantity = Math.Min(max - onFace, source.Available);
        task.Confirmed = 0;
        task.OrganizationIds = item.OrganizationId is null ? [] : [item.OrganizationId];
        task.Reference = source.BalanceId;
        task.AssignedTo = null;
        task.LeaseUntil = null;
        task.ConfirmedOpIds = [];
        task.CreatedAt = timeProvider.GetUtcNow();
        task.StartedAt = null;
        task.CompletedAt = null;
        await session.StoreAsync(task, taskId, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            // A concurrent trigger made the task first.
            return null;
        }
        return task;
    }

    public async Task<int> SweepAsync(string? siteId = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WarehouseLocation> faces;
        using (var session = documentStore.OpenAsyncSession())
            faces = await StockQueries.FixedPickFacesAsync(session, siteId, cancellationToken);

        var created = 0;
        foreach (var face in faces)
        {
            if (await CheckAsync(face.Id!, cancellationToken) is not null)
                created++;
        }
        return created;
    }
}

/// <summary>The daily min/max sweep (<c>Spark:Warehouse:Replenishment:Schedule</c>, default 05:00 UTC; a site-local schedule per Q-W1 through <c>Spark:Cron:Jobs</c>).</summary>
internal sealed partial class WarehouseReplenishmentJob : ISparkCronJob
{
    public static string CronSchedule => "0 5 * * *";

    [Inject] private readonly IWarehouseReplenishment replenishment;
    [Inject] private readonly ILogger<WarehouseReplenishmentJob> logger;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var created = await replenishment.SweepAsync(cancellationToken: cancellationToken);
        logger.LogInformation("Replenishment sweep: {Count} task(s) created.", created);
    }
}
