using System.Linq.Expressions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the RavenDB-provider behaviour that <c>QueryExecutor.RerootOntoIndexQuery</c> depends on
/// (issue #293): a context property's composed query replayed onto an index-backed query must produce
/// RQL carrying <em>both</em> the index and the predicate.
/// </summary>
/// <remarks>
/// <para>
/// `QueryExecutor` reads the context property's queryable and then, whenever an index binding applies,
/// discards it and builds a fresh <c>session.Query&lt;TResult, TIndex&gt;()</c>. A property like
/// <c>Session.Query&lt;T&gt;().Where(x =&gt; x.OwnerId == me)</c> therefore loses its predicate and
/// returns every row, fail-open.
/// </para>
/// <para>
/// These assert on the generated RQL rather than on returned rows, which is why they are kept
/// alongside the behavioural tests in <see cref="ScopedContextPropertyTests"/>: rows can come back
/// correct by accident on a small fixture, and a silently-dropped <c>Where</c> — or a silently-dropped
/// <em>index</em> — is exactly the failure mode. Only the RQL distinguishes them.
/// </para>
/// </remarks>
public class ContextPropertyRerootTests : SparkTestDriver
{
    private sealed class SpikeProbe
    {
        public string? Id { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private sealed class SpikeProbes_ByOwner : AbstractIndexCreationTask<SpikeProbe>
    {
        public SpikeProbes_ByOwner()
        {
            Map = probes => from probe in probes
                            select new { probe.OwnerId, probe.Name };
        }
    }

    /// <summary>Replaces the root queryable of an expression tree with another query's expression.</summary>
    private sealed class RootSwapper(Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node)
            => typeof(IQueryable).IsAssignableFrom(node.Type) ? replacement : node;
    }

    [Fact]
    public async Task A_composed_property_replayed_onto_an_index_query_keeps_both_index_and_predicate()
    {
        await new SpikeProbes_ByOwner().ExecuteAsync(Store);

        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new SpikeProbe { OwnerId = "users/1", Name = "mine" });
            await seed.StoreAsync(new SpikeProbe { OwnerId = "users/2", Name = "theirs" });
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();

        // What a scoped context property returns today.
        var fromProperty = session.Query<SpikeProbe>().Where(p => p.OwnerId == "users/1");

        // What QueryExecutor builds when an index binding applies, discarding the above.
        var indexQuery = session.Query<SpikeProbe, SpikeProbes_ByOwner>();

        var rerooted = (IRavenQueryable<SpikeProbe>)indexQuery.Provider.CreateQuery<SpikeProbe>(
            new RootSwapper(indexQuery.Expression).Visit(fromProperty.Expression));

        var rql = rerooted.ToString();

        // Both halves must survive: the index binding AND the property's predicate.
        rql.Should().Contain("SpikeProbes/ByOwner", "the index binding must survive the re-root");
        rql.Should().Contain("OwnerId", "the property's predicate must survive the re-root");

        var rows = await rerooted.ToListAsync();
        rows.Should().ContainSingle().Which.Name.Should().Be("mine");
    }

    [Fact]
    public async Task A_bare_property_rewrites_to_exactly_the_index_query()
    {
        // The no-regression floor: every context property in the repo today is the bare root, and
        // re-rooting one must produce exactly the plain index query.
        await new SpikeProbes_ByOwner().ExecuteAsync(Store);
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();

        var bare = session.Query<SpikeProbe>();
        var indexQuery = session.Query<SpikeProbe, SpikeProbes_ByOwner>();

        var rerooted = indexQuery.Provider.CreateQuery<SpikeProbe>(
            new RootSwapper(indexQuery.Expression).Visit(bare.Expression));

        rerooted.ToString().Should().Be(indexQuery.ToString());
    }
}
