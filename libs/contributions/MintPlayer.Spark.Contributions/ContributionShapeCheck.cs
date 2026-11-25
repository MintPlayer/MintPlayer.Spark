using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Contributions;

/// <summary>The shape a declaration's current documents were last built in (one per declaration).</summary>
[IdKind(SparkIdKind.Natural)]
internal sealed partial class ContributionShapeMarker
{
    public Id<ContributionShapeMarker> Id { get; set; }
    public string Shape { get; set; } = "";
    public DateTime CheckedAt { get; set; }

    /// <summary>
    /// <c>contributionshapemarkers/{queryname}</c>: lower case (#536 D7) and under its own collection (D23 rule 12), so a
    /// relational table accepts it. A marker written under the old <c>sparkcontributions/shapes/</c> id is not found, so
    /// the first startup after the upgrade rebuilds each declaration's current documents once.
    /// </summary>
    public static Id<ContributionShapeMarker> IdFor(ContributionDescriptor descriptor)
        => Id<ContributionShapeMarker>.FromTrusted(
            SparkCollectionNames.Of(typeof(ContributionShapeMarker)).ToLowerInvariant() + "/" + descriptor.ContributionsQueryName.ToLowerInvariant());
}

/// <summary>
/// At startup, compares every declaration's generated <see cref="ContributionDescriptor.ShapeHash"/> with
/// the one recorded in its marker document, and rebuilds every current document of a declaration whose
/// shape changed (PRD Q6: e.g. <c>History</c> turned on adds <c>ContributionCount</c>). A missing marker
/// counts as changed: a first start rebuilds whatever exists, which is nothing on a new database.
/// </summary>
/// <remarks>
/// Awaited before the host serves requests, so no request reads a current document of the old shape
/// and no rebuild races a request's write. Bounded: the targets are streamed from the two collections
/// once and rebuilt in batches of <see cref="BatchSize"/>, each batch its own session with at most
/// <see cref="SparkContributions.MaxAttempts"/> attempts; the host's start-up cancellation stops it, and
/// the marker is written only after the whole rebuild, so an interrupted one runs again.
/// </remarks>
internal sealed class ContributionShapeCheck(IServiceProvider services, ILogger<ContributionShapeCheck> logger) : IHostedService
{
    internal const int BatchSize = 64;

    private readonly CancellationTokenSource stopping = new();
    private Task? deferred;

    /// <summary>
    /// Runs before the app serves (RavenDB, in-memory: the store is ready at once). A store with a schema is ready only
    /// once <c>UseSpark</c> applied it, after the hosted services started (#536 D41), so there the check runs in the
    /// background as soon as it is.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var readiness = services.GetRequiredService<ISparkStoreReadiness>().WaitAsync(stopping.Token);
        if (readiness.IsCompletedSuccessfully)
            return RunAsync(cancellationToken);
        deferred = Task.Run(async () =>
        {
            try
            {
                await readiness;
                await RunAsync(stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The contributions shape check failed; the current documents were not rebuilt.");
            }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        if (deferred is not null)
            await deferred.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        InitializeModelAssemblies();
        var store = services.GetRequiredService<ISparkStore>();
        var catalog = services.GetRequiredService<ContributionCatalog>();
        var rebuilt = 0;

        foreach (var handler in catalog.All)
        {
            var descriptor = handler.Descriptor;
            var markerId = ContributionShapeMarker.IdFor(descriptor).Value;
            using (var read = store.OpenSession())
            {
                var marker = await read.LoadAsync<ContributionShapeMarker>(markerId, cancellationToken);
                if (marker is not null && marker.Shape == descriptor.ShapeHash)
                    continue;
            }

            IReadOnlyCollection<string> targets;
            using (var scan = store.OpenSession())
                targets = await handler.TargetIdsAsync(scan, cancellationToken);

            if (targets.Count > 0)
                logger.LogInformation("Contributions {Query}: shape {Shape} differs from the stored one; rebuilding the current documents of {Count} target(s).",
                    descriptor.ContributionsQueryName, descriptor.ShapeHash, targets.Count);

            foreach (var batch in targets.Chunk(BatchSize))
                await SparkContributions.RebuildAsync(store, handler, batch, logger, cancellationToken);

            using (var write = store.OpenSession())
            {
                await write.StoreAsync(new ContributionShapeMarker { Shape = descriptor.ShapeHash, CheckedAt = DateTime.UtcNow }, markerId, cancellationToken);
                await write.SaveChangesAsync(cancellationToken);
            }
            rebuilt += targets.Count;
        }

        return rebuilt;
    }

    /// <summary>
    /// Runs the module initializers of the assemblies that hold the model's entity types, so their
    /// declarations are registered before the check reads the registry. Each model type is looked up by
    /// name in the loaded assemblies; nothing is scanned.
    /// </summary>
    private void InitializeModelAssemblies()
    {
        if (services.GetService<IModelLoader>() is not { } models)
            return;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToArray();
        foreach (var clrType in models.GetEntityTypes().Select(e => e.ClrType).OfType<string>().Where(n => n.Length > 0).Distinct())
        {
            foreach (var assembly in assemblies)
            {
                if (assembly.GetType(clrType, throwOnError: false) is { } type)
                {
                    ContributionCatalog.EnsureInitialized(type);
                    break;
                }
            }
        }
    }
}
