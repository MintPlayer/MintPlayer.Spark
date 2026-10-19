using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Live;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Queries;

/// <summary>
/// What an app-stream method receives (<c>docs/guide-live-updates.md</c>): a live query whose rows are not in
/// RavenDB — a ticker, a simulation, an external feed. The method's signature is
/// <c>IAsyncEnumerable&lt;IReadOnlyList&lt;T&gt;&gt; Name(LiveQueryArgs args, CancellationToken ct)</c> (each
/// yield is the <b>whole</b> current set: rows missing from it are removed) or
/// <c>IAsyncEnumerable&lt;T&gt; Name(LiveQueryArgs, CancellationToken)</c> (each yield adds or replaces one row
/// by id).
/// </summary>
/// <remarks>
/// Every yielded batch passes the same row security, redaction and attribute rights as a fetched page, under
/// the viewer's rights at that moment. <see cref="Search"/>, <see cref="Skip"/>, <see cref="Take"/> and
/// <see cref="SortColumns"/> are what the grid asked for; the method may use them to produce less, and the
/// framework applies them to whatever it yields anyway. A grid that changes page, sort or search restarts the
/// method with new arguments.
/// </remarks>
public sealed class LiveQueryArgs
{
    /// <summary>The query being watched.</summary>
    public required SparkQuery Query { get; init; }

    /// <summary>
    /// Who is watching, frozen when the socket connected (commerce PRD X2): the identity key, user and visitor.
    /// There is no <c>HttpContext</c> on a stream; this is what a method may key per-viewer output on.
    /// </summary>
    public required SparkLiveViewer Viewer { get; init; }

    /// <summary>The object whose detail page a sub-query is shown on, resolved and authorized like <c>/execute</c>'s.</summary>
    public PersistentObject? Parent { get; init; }

    /// <summary>The grid's search text, if any.</summary>
    public string? Search { get; init; }

    /// <summary>The first row of the grid's page.</summary>
    public int Skip { get; init; }

    /// <summary>The grid's page size.</summary>
    public int Take { get; init; }

    /// <summary>The sort in effect: the grid's override, else the query's declared order.</summary>
    public IReadOnlyList<SortColumn> SortColumns { get; init; } = [];

    /// <summary>
    /// A session for the method's own reads, held for the stream's life. Its request count is uncapped: a
    /// socket open for minutes is not one request. The framework's per-batch reads use their own sessions.
    /// </summary>
    public required IAsyncDocumentSession Session { get; init; }

    /// <summary>Cancelled when the viewer unsubscribes, changes page/sort/search, or disconnects.</summary>
    public required CancellationToken CancellationToken { get; init; }
}
