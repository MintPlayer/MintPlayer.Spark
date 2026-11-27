using System.Collections.Concurrent;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Records the RQL of every query issued through a store, for tests that assert what was pushed to
/// the server rather than what came back.
/// </summary>
/// <remarks>
/// <b>Attach before the code under test opens its session.</b> RavenDB copies the store's handlers
/// into a session when the session is constructed, so a subscription made afterwards never fires —
/// which reads as "the query never ran" rather than as a test-ordering mistake.
/// <para>
/// This type exists because the obvious form leaks:
/// </para>
/// <code>
/// var queries = new List&lt;string&gt;();
/// Store.OnBeforeQuery += (_, e) =&gt; queries.Add(e.QueryCustomization.ToString()!);
/// </code>
/// <para>
/// That handler is never removed. It is harmless only while the store dies with the test case — on
/// a store shared by a whole class or run, every such subscription survives, accumulates, and
/// captures <em>other</em> tests' RQL into a list that is then asserted with
/// <c>ContainSingle()</c>. Disposing removes the handler, so the recorder cannot outlive its test.
/// </para>
/// <para>
/// The backing store is concurrent: a store may be serving several sessions at once, and Raven
/// makes no promise about which thread raises the event.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var rql = RqlRecorder.Attach(Store);
///
/// await executor.ExecuteQueryAsync(query, search: "alice");
///
/// rql.Queries.Should().ContainSingle().Which.Should().Contain("search(");
/// </code>
/// </example>
/// <remarks>
/// Implements <see cref="IReadOnlyList{T}"/> over the recorded RQL so it reads as the list it
/// replaces — <c>rql.Should().ContainSingle()</c> — rather than forcing every assertion through a
/// property.
/// </remarks>
public sealed class RqlRecorder : IReadOnlyList<string>, IDisposable
{
    private readonly IDocumentStore store;
    private readonly EventHandler<BeforeQueryEventArgs> handler;
    private readonly ConcurrentQueue<string> queries = new();
    private bool disposed;

    private RqlRecorder(IDocumentStore store)
    {
        this.store = store;
        handler = (_, e) => queries.Enqueue(e.QueryCustomization.ToString()!);
        store.OnBeforeQuery += handler;
    }

    /// <summary>Starts recording. Dispose to stop — do not let one outlive its test.</summary>
    public static RqlRecorder Attach(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new RqlRecorder(store);
    }

    /// <summary>
    /// The RQL recorded so far, in order, as a snapshot. Reading it again after more queries run
    /// returns a longer list — it is not a live view, so an assertion cannot observe a query that
    /// arrives mid-assertion.
    /// </summary>
    public IReadOnlyList<string> Queries => [.. queries];

    public int Count => queries.Count;

    public string this[int index] => Queries[index];

    public IEnumerator<string> GetEnumerator() => Queries.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        store.OnBeforeQuery -= handler;
    }
}
