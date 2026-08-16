using System.Diagnostics;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// <c>store.WaitForIndexingAsync()</c> — waits for RavenDB's indexes to catch up, on any
/// <see cref="IDocumentStore"/>.
/// <para>
/// <c>RavenTestDriver</c> already offers this, but only to its own subclasses — which left the
/// tests that need it most without it. Anything driving a <em>real</em> server rather than the
/// embedded test driver (the E2E host, for one) had no equivalent and reached for
/// <c>WaitForNonStaleResults</c> on individual queries instead: easy to forget on the next query
/// someone adds, and silent when forgotten.
/// </para>
/// <para>
/// Originally ported from <c>CronosCore.RavenDB.UnitTests</c>'s <c>VidyanoTestDriver</c>, which has
/// had far more mileage against real indexing behaviour than anything here. Two details are the
/// reason to prefer it over an ad-hoc poll: it <b>throws</b> rather than returning quietly when the
/// indexes never settle, and it <b>reports the actual index errors</b> when they do not. A wait that
/// times out silently converts an indexing problem into a mystery failure somewhere downstream —
/// which is exactly how staleness has surfaced in this repo before.
/// </para>
/// <para>
/// This is the <b>single</b> index-wait implementation in the library;
/// <see cref="RavenIndexHelper.WaitForNonStaleAsync"/> forwards here. Prefer
/// <see cref="SparkTestDriver.SeedAsync"/> where a test owns the write — a server-side wait scoped
/// to the indexes that write actually touched beats polling every index in the database.
/// </para>
/// </summary>
public static class RavenIndexingExtensions
{
    /// <summary>
    /// A side-by-side replacement index exists only while Raven is rebuilding a changed
    /// definition, and queries keep resolving against the <em>old</em> definition until the swap
    /// completes. So its presence must hold up the wait: returning early would hand the caller a
    /// view it is not asking for. The wait ends when Raven removes it as part of the swap.
    /// </summary>
    private const string SideBySideIndexNamePrefix = "ReplacementOf/";

    /// <summary>
    /// How long every index wait in the test library allows before failing. Deliberately not
    /// environment-aware: a wait that needs longer on CI is racing something or genuinely broken,
    /// and a correct wait returns as soon as the condition holds on any machine.
    /// </summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Waits until every enabled index is non-stale and no side-by-side replacement is pending.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The indexes were still stale after <paramref name="timeout"/>, or an index faulted. The
    /// message names the indexes still stale and carries their errors, because "a query returned
    /// the wrong number of rows" is not a diagnosis and this is where the answer actually is.
    /// </exception>
    public static async Task WaitForIndexingAsync(
        this IDocumentStore store,
        string? database = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var db = database ?? store.Database
            ?? throw new ArgumentException("No database specified and store.Database is null.", nameof(database));

        var admin = store.Maintenance.ForDatabase(db);
        var effectiveTimeout = timeout ?? DefaultTimeout;

        var sp = Stopwatch.StartNew();
        DatabaseStatistics? statistics = null;

        while (sp.Elapsed < effectiveTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statistics = await admin.SendAsync(new GetStatisticsOperation(), cancellationToken);

            if (IsSettled(statistics))
                return;

            // A faulted index will never become non-stale, so waiting out the full timeout only
            // delays the report. Break and describe it.
            if (statistics.Indexes.Any(x => x.State == IndexState.Error))
                break;

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(await DescribeFailureAsync(admin, db, statistics, sp.Elapsed, effectiveTimeout, cancellationToken));
    }

    /// <summary>
    /// Settled means: every index that could still catch up has, and no replacement swap is
    /// pending. Disabled indexes are excluded because they never catch up — waiting on one
    /// guarantees a timeout.
    /// </summary>
    private static bool IsSettled(DatabaseStatistics statistics)
        => statistics.Indexes
            .Where(x => x.State != IndexState.Disabled)
            .All(x => !x.IsStale
                && !x.Name.StartsWith(SideBySideIndexNamePrefix, StringComparison.Ordinal));

    private static async Task<string> DescribeFailureAsync(
        MaintenanceOperationExecutor admin,
        string database,
        DatabaseStatistics? statistics,
        TimeSpan elapsed,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var faulted = statistics?.Indexes.Where(x => x.State == IndexState.Error).Select(x => x.Name).ToArray() ?? [];

        // Name what we were actually still waiting on. Without this the message is just
        // "the indexes stayed stale", which tells the reader nothing they can act on.
        var pending = statistics?.Indexes
            .Where(x => x.State != IndexState.Disabled)
            .Where(x => x.IsStale || x.Name.StartsWith(SideBySideIndexNamePrefix, StringComparison.Ordinal))
            .Select(x => x.IsStale ? x.Name : $"{x.Name} (replacement pending)")
            .ToArray() ?? [];

        var reason = faulted.Length > 0
            ? $"Indexes on database '{database}' faulted after {elapsed}: {string.Join(", ", faulted)}."
            : $"Indexes on database '{database}' stayed stale for more than {timeout} (waited {elapsed}).";

        var stillPending = pending.Length > 0
            ? $" Still pending: {string.Join(", ", pending)}."
            : string.Empty;

        return reason + stillPending + await DescribeErrorsAsync(admin, cancellationToken);
    }

    private static async Task<string> DescribeErrorsAsync(
        MaintenanceOperationExecutor admin,
        CancellationToken cancellationToken)
    {
        var errors = await admin.SendAsync(new GetIndexErrorsOperation(), cancellationToken);

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
