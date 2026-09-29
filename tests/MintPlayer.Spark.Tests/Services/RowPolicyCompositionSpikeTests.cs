using System.Linq.Expressions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Spikes S1, S2 and S3 of #460 (recorded in <c>docs/issue_460_PRD.md</c> §4), kept as regression
/// tests because they pin the query shapes the row-policy seam depends on.
/// <list type="bullet">
/// <item>S1 — two rebound predicates joined with <c>AndAlso</c>, then a column filter, then a search
/// group, translate as <c>(A and B) and column and (search or search)</c>: no <c>Invoke</c>, no
/// search-options leak.</item>
/// <item>S2 — an entity filter rebound onto an index projection by member name pushes down, with
/// paging and totals correct under 30% deleted rows; a projection lacking a member refuses the
/// rebinding, so the caller falls back to the post-filter.</item>
/// <item>S3 — <c>IsDeleted != true</c> versus <c>!IsDeleted</c> on documents that lack the field, and
/// the interface-cast shape versus the property shape.</item>
/// </list>
/// Each runs on a real database with documents, an index and a query, on both engines.
/// </summary>
public abstract class RowPolicyCompositionSpikeTests : SparkTestDriver
{
    protected abstract string Engine { get; }

    protected override void PreConfigureDatabase(DatabaseRecord databaseRecord)
    {
        base.PreConfigureDatabase(databaseRecord);
        databaseRecord.Settings ??= new Dictionary<string, string>();
        databaseRecord.Settings["Indexing.Auto.SearchEngineType"] = Engine;
        databaseRecord.Settings["Indexing.Static.SearchEngineType"] = Engine;
    }

    // ---- fixture types ----------------------------------------------------------------------

    public interface ISpikeSoftDeletable
    {
        bool IsDeleted { get; set; }
    }

    public class SpikeDoc : ISpikeSoftDeletable
    {
        public string? Id { get; set; }
        public string Owner { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
    }

    /// <summary>The same collection, written by code that predates the field.</summary>
    public class LegacySpikeDoc
    {
        public string? Id { get; set; }
        public string Owner { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }

    public class SpikeRow
    {
        public string? Id { get; set; }
        public string Owner { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
    }

    /// <summary>A projection that does not carry <c>Owner</c>.</summary>
    public class SpikeRowWithoutOwner
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
    }

    public class SpikeDocs_Overview : AbstractIndexCreationTask<SpikeDoc>
    {
        public SpikeDocs_Overview()
        {
            Map = docs => from d in docs
                          select new { d.Owner, d.Category, d.Title, d.Body, d.IsDeleted };
            Index(x => x.Title, FieldIndexing.Search);
            Index(x => x.Body, FieldIndexing.Search);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    // ---- seed -------------------------------------------------------------------------------

    /// <summary>
    /// 20 alice rows (6 deleted, 10 explicitly not deleted, 4 without the field at all) + 5 bob rows.
    /// Titles alternate apple/pear, categories alternate A/B.
    /// </summary>
    private async Task SeedAsync()
    {
        await new SpikeDocs_Overview().ExecuteAsync(Store);

        var collection = Store.Conventions.GetCollectionName(typeof(SpikeDoc));
        using (var session = Store.OpenAsyncSession())
        {
            for (var i = 0; i < 16; i++)
            {
                await session.StoreAsync(new SpikeDoc
                {
                    Id = $"spikedocs/a{i:00}",
                    Owner = "alice",
                    Category = i % 2 == 0 ? "A" : "B",
                    Title = i % 4 < 2 ? "apple tart" : "pear cake",
                    Body = "text",
                    IsDeleted = i < 6,
                });
            }
            for (var i = 0; i < 5; i++)
            {
                await session.StoreAsync(new SpikeDoc
                {
                    Id = $"spikedocs/b{i:00}",
                    Owner = "bob",
                    Category = "A",
                    Title = "apple tart",
                    Body = "text",
                });
            }
            await session.SaveChangesAsync();
        }

        // Absent field: stored through a type without IsDeleted, into the same collection.
        using (var session = Store.OpenAsyncSession())
        {
            for (var i = 0; i < 4; i++)
            {
                var legacy = new LegacySpikeDoc
                {
                    Id = $"spikedocs/l{i:00}",
                    Owner = "alice",
                    Category = i % 2 == 0 ? "A" : "B",
                    Title = i < 2 ? "apple tart" : "pear cake",
                    Body = "apple text",
                };
                await session.StoreAsync(legacy);
                session.Advanced.GetMetadataFor(legacy)["@collection"] = collection;
            }
            await session.SaveChangesAsync();
        }

        WaitForIndexing(Store);
    }

    // ---- predicates -------------------------------------------------------------------------

    private static readonly Expression<Func<SpikeDoc, bool>> ActionsFilter = d => d.Owner == "alice";
    private static readonly Expression<Func<ISpikeSoftDeletable, bool>> PolicyNotTrue = x => x.IsDeleted != true;
    private static readonly Expression<Func<ISpikeSoftDeletable, bool>> PolicyNegated = x => !x.IsDeleted;

    /// <summary>The policy rebound by the framework's helper (property shape) and AND-ed with the actions filter.</summary>
    private static Expression<Func<SpikeDoc, bool>> Composed(Expression<Func<ISpikeSoftDeletable, bool>> policy)
        => (Expression<Func<SpikeDoc, bool>>)RowFilterExpressions.Combine(
            typeof(SpikeDoc), [(ActionsFilter, "actions"), (policy, "policy")])!;

    /// <summary>The same composition in the interface-cast shape: <c>((ISpikeSoftDeletable)row).IsDeleted</c>.</summary>
    private static Expression<Func<SpikeDoc, bool>> ComposedWithCast(Expression<Func<ISpikeSoftDeletable, bool>> policy)
    {
        var row = Expression.Parameter(typeof(SpikeDoc), "row");
        var cast = new Swap(policy.Parameters[0], Expression.Convert(row, typeof(ISpikeSoftDeletable))).Visit(policy.Body);
        var actions = new Swap(ActionsFilter.Parameters[0], row).Visit(ActionsFilter.Body);
        return Expression.Lambda<Func<SpikeDoc, bool>>(Expression.AndAlso(actions, cast), row);
    }

    private sealed class Swap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    private static readonly string[] AliceLiveApplesInA = ["spikedocs/a08", "spikedocs/a12", "spikedocs/l00", "spikedocs/l02"];

    // ---- S1 ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S1_rebound_filters_then_column_filter_then_search_translate_as_one_AND_group(bool staticIndex)
    {
        await SeedAsync();
        using var session = Store.OpenAsyncSession();

        var source = staticIndex ? session.Query<SpikeDoc, SpikeDocs_Overview>() : session.Query<SpikeDoc>();
        var query = source
            .Where(Composed(PolicyNotTrue))
            .Where(d => d.Category == "A")
            .Search(d => d.Title, "apple")
            .Search(d => d.Body, "apple");

        var rql = query.ToString();
        var ids = (await query.ToListAsync()).Select(d => d.Id).OrderBy(i => i).ToList();

        rql.Should().NotContain("Invoke");
        rql.Should().EndWith("where ((Owner = $p0 and IsDeleted != $p1)) and (Category = $p2) and (search(Title, $p3) or search(Body, $p4))");
        ids.Should().Equal(AliceLiveApplesInA);

        await AssertEngineAsync(staticIndex, session);
    }

    // ---- S3 ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3_not_true_includes_rows_without_the_field_and_negation_drops_them(bool staticIndex)
    {
        await SeedAsync();
        using var session = Store.OpenAsyncSession();

        IRavenQueryable<SpikeDoc> Source() => staticIndex ? session.Query<SpikeDoc, SpikeDocs_Overview>() : session.Query<SpikeDoc>();

        var notTrue = await Source().Where(Composed(PolicyNotTrue)).Select(d => d.Id).ToListAsync();
        var negated = await Source().Where(Composed(PolicyNegated)).Select(d => d.Id).ToListAsync();

        notTrue.Should().HaveCount(14, "10 explicit false + 4 without the field");
        notTrue.Where(id => id!.StartsWith("spikedocs/l")).Should().HaveCount(4);
        negated.Where(id => id!.StartsWith("spikedocs/l")).Should().BeEmpty(
            "!x.IsDeleted translates to IsDeleted = false, which a document without the field does not match");
        negated.Should().HaveCount(10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3_interface_cast_shape_versus_property_shape(bool staticIndex)
    {
        await SeedAsync();
        using var session = Store.OpenAsyncSession();

        IRavenQueryable<SpikeDoc> Source() => staticIndex ? session.Query<SpikeDoc, SpikeDocs_Overview>() : session.Query<SpikeDoc>();

        var property = Source().Where(Composed(PolicyNotTrue));
        property.ToString().Should().Contain("IsDeleted != $p1");
        (await property.ToListAsync()).Should().HaveCount(14);

        // The cast shape: record whether the provider translates it, and to what.
        var cast = Source().Where(ComposedWithCast(PolicyNotTrue));
        string? castRql = null;
        int? castCount = null;
        Exception? castError = null;
        try
        {
            castRql = cast.ToString();
            castCount = (await cast.ToListAsync()).Count;
        }
        catch (Exception ex)
        {
            castError = ex;
        }

        // Measured on RavenDB 7.2: the provider strips the interface cast and emits the same RQL.
        castError.Should().BeNull();
        castRql.Should().Be(property.ToString());
        castCount.Should().Be(14);
    }

    [Fact]
    public async Task S3_in_memory_detail_and_distinct_paths_evaluate_both_shapes_on_a_row_without_the_field()
    {
        // Detail checks and distinct values evaluate the compiled predicate over a materialized
        // document. A document without the field materializes with the CLR default (false).
        await SeedAsync();
        using var session = Store.OpenAsyncSession();
        var legacy = await session.LoadAsync<SpikeDoc>("spikedocs/l00");

        Composed(PolicyNotTrue).Compile()(legacy).Should().BeTrue();
        Composed(PolicyNegated).Compile()(legacy).Should().BeTrue(
            "in memory the two shapes agree; they diverge only in the database, which is the trap");
    }

    // ---- S2 ---------------------------------------------------------------------------------

    [Fact]
    public async Task S2_entity_filter_rebound_onto_projection_pushes_down_with_correct_paging_and_totals()
    {
        await SeedAsync();
        using var session = Store.OpenAsyncSession();

        var rebound = (Expression<Func<SpikeRow, bool>>?)RowFilterExpressions.Rebind(Composed(PolicyNotTrue), typeof(SpikeRow));
        rebound.Should().NotBeNull();

        // The order Spark's query executor uses: projection first, then the row filter.
        var query = session.Query<SpikeRow, SpikeDocs_Overview>()
            .ProjectInto<SpikeRow>()
            .Where(rebound!)
            .Statistics(out var stats)
            .OrderBy(r => r.Title)
            .ThenBy(r => r.Id);

        query.ToString().Should().Contain("where (Owner = $p0 and IsDeleted != $p1) order by Title, id() select id() as Id, Owner, Title, IsDeleted");

        var page = await query.Skip(10).Take(5).ToListAsync();
        stats.TotalResults.Should().Be(14, "25 rows, 5 bob, 6 deleted: the total counts only what the caller may see");
        page.Should().HaveCount(4, "rows 11-14 of 14");

        var all = await session.Query<SpikeRow, SpikeDocs_Overview>().ProjectInto<SpikeRow>().Where(rebound!).ToListAsync();
        all.Should().HaveCount(14);
        all.Should().OnlyContain(r => r.Owner == "alice" && !r.IsDeleted);
    }

    [Fact]
    public void S2_a_projection_without_a_filtered_member_refuses_the_rebinding()
    {
        RowFilterExpressions.Rebind(Composed(PolicyNotTrue), typeof(SpikeRowWithoutOwner)).Should().BeNull(
            "Owner is not on the projection, so the caller must fall back to the post-filter");
        RowFilterExpressions.Rebind(PolicyNotTrue, typeof(SpikeRowWithoutOwner)).Should().NotBeNull(
            "a policy that reads only IsDeleted still rebinds");
    }

    private async Task AssertEngineAsync(bool staticIndex, IAsyncDocumentSession session)
    {
        var indexes = await Store.Maintenance.SendAsync(new GetIndexesStatisticsOperation());
        var used = staticIndex
            ? indexes.Single(i => i.Name == new SpikeDocs_Overview().IndexName)
            : indexes.First(i => i.Name.StartsWith("Auto/"));
        used.SearchEngineType.ToString().Should().Be(Engine);
    }
}

public sealed class RowPolicyCompositionSpikeTests_Corax : RowPolicyCompositionSpikeTests
{
    protected override string Engine => "Corax";
}

public sealed class RowPolicyCompositionSpikeTests_Lucene : RowPolicyCompositionSpikeTests
{
    protected override string Engine => "Lucene";
}
