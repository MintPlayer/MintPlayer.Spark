using System.Diagnostics;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// <c>store.WaitForIndexing()</c> — waits for RavenDB's indexes to catch up, on any
/// <see cref="IDocumentStore"/>.
/// <para>
/// <c>RavenTestDriver</c> already offers this, but only to its own subclasses — which left the
/// tests that need it most without it. Anything driving a <em>real</em> server rather than the
/// embedded test driver (the E2E host, for one) had no equivalent and reached for
/// <c>WaitForNonStaleResults</c> on individual queries instead: easy to forget on the next query
/// someone adds, and silent when forgotten.
/// </para>
/// <para>
/// Ported from <c>CronosCore.RavenDB.UnitTests</c>'s <c>VidyanoTestDriver</c>, which has had far
/// more mileage against real indexing behaviour than anything here. Two details are the reason to
/// prefer it over an ad-hoc poll: it <b>throws</b> rather than returning quietly when the indexes
/// never settle, and it <b>reports the actual index errors</b> when they do not. A wait that
/// times out silently converts an indexing problem into a mystery failure somewhere downstream —
/// which is exactly how staleness has surfaced in this repo before.
/// </para>
/// </summary>
public static class RavenIndexingExtensions
{
    /// <summary>Side-by-side index replacements are transient and must not hold up a wait.</summary>
    private const string SideBySideIndexNamePrefix = "ReplacementOf/";

    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Blocks until every enabled index is non-stale.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The indexes were still stale after <paramref name="timeout"/>, or an index faulted. The
    /// message carries the index errors, because "a query returned the wrong number of rows" is
    /// not a diagnosis and this is where the answer actually is.
    /// </exception>
    public static void WaitForIndexing(
        this IDocumentStore store,
        string? database = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var admin = store.Maintenance.ForDatabase(database ?? store.Database);
        timeout ??= DefaultTimeout;

        var sp = Stopwatch.StartNew();

        while (sp.Elapsed < timeout.Value)
        {
            var statistics = admin.Send(new GetStatisticsOperation());

            var indexes = statistics.Indexes.Where(x => x.State != IndexState.Disabled);

            if (indexes.All(x => !x.IsStale
                && !x.Name.StartsWith(SideBySideIndexNamePrefix, StringComparison.Ordinal)))
            {
                return;
            }

            // A faulted index will never become non-stale, so waiting out the full timeout only
            // delays the report. Break and describe it.
            if (statistics.Indexes.Any(x => x.State == IndexState.Error))
                break;

            Thread.Sleep(100);
        }

        throw new TimeoutException(
            $"The indexes stayed stale for more than {timeout.Value}.{DescribeErrors(admin)}");
    }

    /// <summary>Async-friendly wrapper; the underlying maintenance calls are synchronous.</summary>
    public static Task WaitForIndexingAsync(
        this IDocumentStore store,
        string? database = null,
        TimeSpan? timeout = null)
        => Task.Run(() => store.WaitForIndexing(database, timeout));

    private static string DescribeErrors(MaintenanceOperationExecutor admin)
    {
        var errors = admin.Send(new GetIndexErrorsOperation());

        if (errors is not { Length: > 0 })
            return string.Empty;

        var described = errors
            .Where(e => e.Errors.Length > 0)
            .Select(e =>
            {
                var lines = string.Join(Environment.NewLine, e.Errors.Select(x => $"- {x}"));
                return $"Index '{e.Name}' ({e.Errors.Length} errors):{Environment.NewLine}{lines}";
            })
            .ToArray();

        return described.Length == 0
            ? string.Empty
            : $" Indexing errors:{Environment.NewLine}{string.Join(Environment.NewLine, described)}";
    }
}
