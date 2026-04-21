using System.Reflection;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Public index/indexing helpers usable from any test context — <see cref="SparkTestDriver"/>
/// subclasses, the Fleet E2E host, or plain <c>IDocumentStore</c>-holding fixtures. Exists
/// because <see cref="Raven.TestDriver.RavenTestDriver.WaitForIndexing"/> is protected and
/// therefore not reachable from code that doesn't derive from <c>RavenTestDriver</c>.
/// </summary>
public static class RavenIndexHelper
{
    /// <summary>
    /// Polls <see cref="GetStatisticsOperation"/> until the target database reports no stale
    /// indexes, or throws <see cref="TimeoutException"/> if <paramref name="timeout"/> elapses.
    /// Errors out immediately if any index has moved to <see cref="IndexState.Error"/> — a
    /// silent hang on a failed index is the worst possible test-infra failure mode.
    /// </summary>
    public static async Task WaitForNonStaleAsync(
        IDocumentStore store,
        string? database = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var db = database ?? store.Database
            ?? throw new ArgumentException("No database specified and store.Database is null.", nameof(database));
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(1);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        var maintenance = store.Maintenance.ForDatabase(db);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stats = await maintenance.SendAsync(new GetStatisticsOperation(), cancellationToken);

            var erroredIndex = stats.Indexes.FirstOrDefault(i => i.State == IndexState.Error);
            if (erroredIndex is not null)
            {
                throw new InvalidOperationException(
                    $"Index '{erroredIndex.Name}' on database '{db}' is in error state. " +
                    "Check the Raven Studio indexes page for the underlying exception.");
            }

            if (stats.StaleIndexes.Length == 0)
                return;

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Indexes on database '{db}' did not settle within {effectiveTimeout}. " +
                    $"Still stale: {string.Join(", ", stats.StaleIndexes)}");
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    /// <summary>
    /// Registers every <see cref="AbstractIndexCreationTask"/> found in the supplied assemblies
    /// and then waits for the server to finish building them. Equivalent in spirit to the
    /// "wait for all async index creations" step described by CronosCore's Readme — a test's
    /// first query is free to assume its indexes are live once this returns.
    /// </summary>
    public static async Task DeployIndexesAsync(
        IDocumentStore store,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (assemblies.Length == 0) return;

        foreach (var assembly in assemblies)
        {
            await IndexCreation.CreateIndexesAsync(assembly, store);
        }

        await WaitForNonStaleAsync(store);
    }
}
