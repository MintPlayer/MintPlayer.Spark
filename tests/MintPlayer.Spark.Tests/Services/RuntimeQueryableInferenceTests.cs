using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The RavenDB provider behaviour that #294's fix rests on, pinned against a real server.
/// <para>
/// <c>QueryExecutor</c> infers a custom query's capabilities from the <em>runtime result</em> rather
/// than the declared return type, because the declared type is routinely weaker than the object —
/// <c>session.Query&lt;T&gt;()</c> assigned to <c>IQueryable&lt;T&gt;</c> is the common idiom. That
/// only works if Raven's queryable really does implement <c>IRavenQueryable&lt;T&gt;</c>, which is a
/// fact about Raven, not about Spark.
/// </para>
/// <para>
/// Measured: both shapes are <c>RavenQueryInspector&lt;T&gt;</c>, an in-memory queryable is
/// <c>EnumerableQuery&lt;T&gt;</c>. If a future RavenDB upgrade returns a wrapper that no longer
/// implements the generic interface, every async custom query silently loses its pushdown again and
/// this test is the only thing that would notice.
/// </para>
/// </summary>
public class RuntimeQueryableInferenceTests : SparkTestDriver
{
    private class Thing
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>The declared-weaker-than-actual shape: typed IQueryable, backed by Raven.</summary>
    private IQueryable<Thing> DeclaredAsQueryable(Raven.Client.Documents.Session.IAsyncDocumentSession s)
        => s.Query<Thing>();

    private async Task<IQueryable<Thing>> DeclaredAsQueryableAsync(Raven.Client.Documents.Session.IAsyncDocumentSession s)
        => await Task.FromResult<IQueryable<Thing>>(s.Query<Thing>());

    [Fact]
    public async Task Runtime_inference_recognises_a_raven_queryable_behind_a_weaker_declared_type()
    {
        using var session = Store.OpenAsyncSession();
        var generic = typeof(IRavenQueryable<>).MakeGenericType(typeof(Thing));

        object sync = DeclaredAsQueryable(session);
        object asyncResult = await DeclaredAsQueryableAsync(session);
        object inMemory = new[] { new Thing { Id = "things/1" } }.AsQueryable();

        // The claim D1 depends on, for both the sync and the awaited shape.
        generic.IsInstanceOfType(sync).Should().BeTrue(
            "a session.Query<T>() declared as IQueryable<T> must still be recognised as Raven-backed");
        generic.IsInstanceOfType(asyncResult).Should().BeTrue(
            "awaiting must not change what the object is");

        // And the boundary: an in-memory queryable must NOT be mistaken for a Raven one, or the
        // fix would route it to ExecuteQueryableAsync and fail.
        generic.IsInstanceOfType(inMemory).Should().BeFalse();
        (inMemory is IQueryable).Should().BeTrue();

        Dump($"sync={sync.GetType().FullName}");
        Dump($"async={asyncResult.GetType().FullName}");
        Dump($"inMemory={inMemory.GetType().FullName}");
    }

    private static void Dump(string message) => Console.WriteLine($"[S1] {message}");
}
