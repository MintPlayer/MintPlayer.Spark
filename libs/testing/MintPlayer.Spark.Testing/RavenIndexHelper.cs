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
    /// <exception cref="RavenIndexDeploymentException">An index faulted; the message carries its errors.</exception>
    /// <exception cref="TimeoutException">The indexes were healthy but still stale after <paramref name="timeout"/>.</exception>
    public static Task WaitForNonStaleAsync(
        IDocumentStore store,
        string? database = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => store.WaitForIndexingAsync(database, timeout, cancellationToken: cancellationToken);

    /// <summary>
    /// Registers every <see cref="AbstractIndexCreationTask"/> found in the supplied assemblies
    /// and then waits for the server to finish building them — a test's first query is free
    /// to assume its indexes are live once this returns.
    /// <para>
    /// Waiting for non-stale alone is not enough on a fresh database, which is the only kind this
    /// runs against. With no documents yet, "nothing is stale" is satisfied trivially — and worse,
    /// there is a window right after registration where the new definition has not reached
    /// <c>DatabaseStatistics</c> at all, so the wait can return before the index even exists. So
    /// confirm the expected definitions are present first, then wait for them to settle.
    /// </para>
    /// </summary>
    /// <exception cref="RavenIndexDeploymentException">
    /// A declared index never appeared on the server, or faulted while building.
    /// </exception>
    /// <exception cref="TimeoutException">The indexes deployed but never settled.</exception>
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

        // One wait covering both halves: the declared definitions are registered AND everything is
        // up to date. Waiting only for non-stale would be vacuous here — this always runs against
        // a fresh database, where "no index is stale" is trivially true because there are no
        // indexes yet, so the wait could return before the definitions existed at all.
        await store.WaitForIndexingAsync(
            expectedIndexes: DeclaredIndexNames(assemblies));
    }

    /// <summary>
    /// The index names an assembly declares, resolved the same way
    /// <see cref="IndexCreation"/> discovers them: concrete
    /// <see cref="AbstractIndexCreationTask"/> types with a parameterless constructor.
    /// </summary>
    public static IReadOnlyCollection<string> DeclaredIndexNames(params Assembly[] assemblies)
        => assemblies.SelectMany(DeclaredIndexNamesCore).ToArray();

    private static IEnumerable<string> DeclaredIndexNamesCore(Assembly assembly)
        => assembly.GetTypes()
            .Where(t => typeof(AbstractIndexCreationTask).IsAssignableFrom(t)
                && t is { IsAbstract: false, IsGenericTypeDefinition: false }
                && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (AbstractIndexCreationTask)Activator.CreateInstance(t)!)
            .Select(t => t.IndexName);
}
