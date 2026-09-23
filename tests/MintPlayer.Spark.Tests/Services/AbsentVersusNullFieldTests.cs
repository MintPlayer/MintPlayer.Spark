using MintPlayer.Assertions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Whether <c>== null</c> finds a field the document never wrote — the round-trip the distinct panel's
/// <c>&lt; none &gt;</c> entry depends on.
/// </summary>
/// <remarks>
/// The panel builds its values from <em>materialized rows</em>, where a field the document omits and a
/// field written as JSON null are indistinguishable: both arrive as a null CLR property and both
/// present as <c>&lt; none &gt;</c>. The filter then round-trips that choice as a query. If the query
/// cannot express "absent", the panel is offering a value it cannot honour.
/// <para>
/// This repo has been bitten by the neighbouring case twice — an absent JSON field does not satisfy
/// <c>== false</c> or <c>!x</c>, which is why <c>Commits_ByRepository.Result.ContributedFromFork</c>
/// carries a long warning to test <c>!= true</c> instead. That bug emptied every grid for
/// pre-existing repositories while the whole suite stayed green, because every fixture wrote the field.
/// These fixtures deliberately do not.
/// </para>
/// </remarks>
public class AbsentVersusNullFieldTests : SparkTestDriver
{
    public class NullProbe
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public int? Rating { get; set; }
    }

    public class NullProbes_Overview : AbstractIndexCreationTask<NullProbe>
    {
        public NullProbes_Overview()
        {
            Map = probes => from p in probes select new { p.Label, p.Rating };
        }
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new NullProbes_Overview().ExecuteAsync(Store);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new NullProbe { Id = "nullprobes/rated", Label = "rated", Rating = 5 });
            await session.StoreAsync(new NullProbe { Id = "nullprobes/explicit", Label = "explicit", Rating = null });
            await session.StoreAsync(new NullProbe { Id = "nullprobes/absent", Label = "absent", Rating = null });
        });

        // Strip the property from one document so it is genuinely ABSENT rather than written as null.
        // That is the shape a real collection acquires when a property is added to an entity after
        // documents already exist — exactly the case the ContributedFromFork incident was about.
        var patch = Store.Operations.Send(new PatchByQueryOperation(new IndexQuery
        {
            Query = "from NullProbes as p where id() = 'nullprobes/absent' update { delete p.Rating; }",
        }));
        await patch.WaitForCompletionAsync();

        await Store.WaitForIndexingAsync();
    }

    private async Task<string> LabelsAsync(Func<IRavenQueryable<NullProbe>, IQueryable<NullProbe>> shape)
    {
        using var session = Store.OpenAsyncSession();
        var rows = await shape(session.Query<NullProbe, NullProbes_Overview>()).ToListAsync();
        return string.Join(",", rows.Select(r => r.Label).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_patch_really_removed_the_field()
    {
        // Without this the whole class could pass vacuously against three documents that all still
        // carry the property.
        var all = await LabelsAsync(q => q);
        all.Should().Be("absent,explicit,rated", "all three documents are still in the collection");
    }

    /// <summary>
    /// ⚠️ <b>An absent field is not <c>== null</c> — it is on the <c>!= null</c> side.</b> This is the
    /// constraint that makes the distinct panel's <c>&lt; none &gt;</c> entry unhonourable, recorded as
    /// D2 / R1 in <c>docs/subquery_column_filters_plan.md</c>.
    /// </summary>
    /// <remarks>
    /// The grid draws both rows blank and the panel offers one <c>&lt; none &gt;</c> for the pair, but
    /// no predicate can select that pair: ticking the value drops a row the grid drew as blank, and
    /// excluding it keeps one. These assertions state RavenDB's behaviour, not Spark's desired
    /// behaviour — if a future RavenDB changes it, this test is where you find out.
    /// </remarks>
    [Fact]
    public async Task An_absent_field_is_not_equal_to_null_and_counts_as_not_null()
    {
        (await LabelsAsync(q => q.Where(x => x.Rating == null))).Should().Be("explicit",
            "only a field written as JSON null carries the null term; an absent one carries no term");

        (await LabelsAsync(q => q.Where(x => x.Rating != null))).Should().Be("absent,rated",
            "so the absent row lands with the rows that DO have a value — the wrong side of the split");
    }

    /// <summary>
    /// ⛳ The complement <b>is</b> expressible — which is what the client's repair rests on.
    /// </summary>
    /// <remarks>
    /// "No value" cannot be asked for directly, but it is exactly "none of the real values", and that
    /// is an ordinary exclusion. So the filter panel sends a <c>&lt; none &gt;</c> selection as an
    /// <c>excludes</c> of everything it did not select, which needs no server change at all.
    /// <para>
    /// ⚠️ Only sound when the distinct list is complete. The client keeps the list solely when the
    /// server reported <c>hasMore == false</c> and no search term narrowed it, because a complement of
    /// a partial list excludes the wrong set — silently.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Excluding_every_real_value_selects_the_absent_and_the_null_rows_together()
    {
        var matched = await LabelsAsync(q => q.Where(x => x.Rating != 5));

        matched.Should().Be("absent,explicit",
            "the complement of the real values is the pair the grid draws blank — the set `< none >` " +
            "means, reached without ever naming null");
    }

    /// <summary>
    /// ⚠️ <c>HasValue</c> is not a way out, and it is a live 500 hazard in its own right.
    /// </summary>
    /// <remarks>
    /// It is translated as a <em>field name</em>, <c>Rating_HasValue</c>, which no Map emits — so the
    /// whole query throws <c>ArgumentException</c> and the grid 500s. Worth knowing beyond this
    /// defect: any hand-written row filter or custom query that tests <c>x.Foo.HasValue</c> against a
    /// static index fails the same way, and nothing warns.
    /// </remarks>
    [Fact]
    public async Task No_HasValue_shape_reaches_an_absent_field_and_one_of_them_throws()
    {
        // The negated form collapses to the same query as `== null`, so it does not reach the absent
        // row either — there is no LINQ shape that does.
        (await LabelsAsync(q => q.Where(x => !x.Rating.HasValue))).Should().Be("explicit",
            "!HasValue is just `== null` by another name; absent stays unreachable");

        // The compared form is worse: it becomes a FIELD NAME the index does not emit.
        Exception? thrown = null;
        try { await LabelsAsync(q => q.Where(x => x.Rating.HasValue == false)); }
        catch (Exception ex) { thrown = ex; }

        thrown.Should().NotBeNull("`HasValue == false` must not silently return a wrong row set");
        thrown!.ToString().Should().Contain("Rating_HasValue",
            "it is translated as the field 'Rating_HasValue', which no Map emits, so the whole query 500s");
    }
}
