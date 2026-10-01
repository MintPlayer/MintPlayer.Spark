using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Contributions;

/// <summary>The shape a declaration's current documents were last built in (one per declaration).</summary>
internal sealed class ContributionShapeMarker
{
    public string? Id { get; set; }
    public string Shape { get; set; } = "";
    public DateTime CheckedAt { get; set; }

    /// <summary><c>SparkContributions/Shapes/{QueryName}</c>.</summary>
    public static string IdFor(ContributionDescriptor descriptor) => "SparkContributions/Shapes/" + descriptor.ContributionsQueryName;
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

    public Task StartAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        InitializeModelAssemblies();
        var store = services.GetRequiredService<IDocumentStore>();
        var catalog = services.GetRequiredService<ContributionCatalog>();
        var rebuilt = 0;

        foreach (var handler in catalog.All)
        {
            var descriptor = handler.Descriptor;
            var markerId = ContributionShapeMarker.IdFor(descriptor);
            using (var read = store.OpenAsyncSession())
            {
                var marker = await read.LoadAsync<ContributionShapeMarker>(markerId, cancellationToken);
                if (marker is not null && marker.Shape == descriptor.ShapeHash)
                    continue;
            }

            IReadOnlyCollection<string> targets;
            using (var scan = store.OpenAsyncSession())
                targets = await handler.TargetIdsAsync(scan, cancellationToken);

            if (targets.Count > 0)
                logger.LogInformation("Contributions {Query}: shape {Shape} differs from the stored one; rebuilding the current documents of {Count} target(s).",
                    descriptor.ContributionsQueryName, descriptor.ShapeHash, targets.Count);

            foreach (var batch in targets.Chunk(BatchSize))
                await SparkContributions.RebuildAsync(store, handler, batch, logger, cancellationToken);

            using (var write = store.OpenAsyncSession())
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
