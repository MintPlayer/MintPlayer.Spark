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
    /// Waits until the target database reports no stale indexes.
    /// <para>
    /// Forwards to <see cref="RavenIndexingExtensions.WaitForIndexingAsync"/>, which is the single
    /// implementation. This entry point used to have its own loop with a different done-condition
    /// (the server's <c>StaleIndexes</c> list rather than per-index state), no filtering of
    /// disabled indexes, no handling of side-by-side swaps, a different exception type, and its own
    /// separately-declared one-minute default — so which of the two a test happened to call changed
    /// what "settled" meant and what a failure told you.
    /// </para>
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The indexes were still stale after <paramref name="timeout"/>, or an index faulted; the
    /// message names them and carries their errors.
    /// </exception>
    public static Task WaitForNonStaleAsync(
        IDocumentStore store,
        string? database = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => store.WaitForIndexingAsync(database, timeout, cancellationToken);

    /// <summary>
    /// Registers every <see cref="AbstractIndexCreationTask"/> found in the supplied assemblies
    /// and then waits for the server to finish building them — a test's first query is free
    /// to assume its indexes are live once this returns.
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
