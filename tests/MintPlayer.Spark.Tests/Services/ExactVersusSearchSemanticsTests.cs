using MintPlayer.Assertions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// What <c>FieldIndexing.Exact</c> and <c>FieldIndexing.Search</c> actually do to the four operations
/// Spark performs on a field, measured rather than reasoned about.
/// </summary>
public class ExactVersusSearchSemanticsTests : SparkTestDriver
{
    public class SemanticsProbe
    {
        public string? Id { get; set; }

        /// <summary>In the map, no <c>Index(...)</c> call at all.</summary>
        public string Plain { get; set; } = string.Empty;

        /// <summary><c>FieldIndexing.Search</c> — analyzed.</summary>
        public string Searched { get; set; } = string.Empty;

        /// <summary><c>FieldIndexing.Exact</c>.</summary>
        public string Exacted { get; set; } = string.Empty;
    }

    /// <summary>Distinctive prefix: a colliding index class name silently redefines the deployed index.</summary>
    public class SemanticsProbes_Overview : AbstractIndexCreationTask<SemanticsProbe>
    {
        public SemanticsProbes_Overview()
        {
            Map = probes => from p in probes select new { p.Plain, p.Searched, p.Exacted };
            Index(nameof(SemanticsProbe.Searched), FieldIndexing.Search);
            Index(nameof(SemanticsProbe.Exacted), FieldIndexing.Exact);
        }
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new SemanticsProbes_Overview().ExecuteAsync(Store);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new SemanticsProbe { Plain = "Alpha Bravo", Searched = "Alpha Bravo", Exacted = "Alpha Bravo" });
            await session.StoreAsync(new SemanticsProbe { Plain = "Charlie Delta", Searched = "Charlie Delta", Exacted = "Charlie Delta" });
        });
        await Store.WaitForIndexingAsync();
    }

    private async Task<string> ProbeAsync(Func<IRavenQueryable<SemanticsProbe>, IQueryable<SemanticsProbe>> shape)
    {
        try
        {
            using var session = Store.OpenAsyncSession();
            var query = session.Query<SemanticsProbe, SemanticsProbes_Overview>();
            return (await shape(query).ToListAsync()).Count.ToString();
        }
        catch (Exception ex)
        {
            return "throws:" + ex.GetType().Name;
        }
    }

    /// <summary>
    /// ⚠️ <c>Exact</c> is <b>not</b> a way to get search. It answers the opposite half of the question,
    /// and the refusal is silent.
    /// </summary>
    [Fact]
    public async Task Search_matches_a_single_term_only_on_an_analyzed_field()
    {
        (await ProbeAsync(q => q.Search(x => x.Searched, "Bravo"))).Should().Be("1",
            "an analyzed field is tokenized, so one word of the value matches");

        // Neither of these throws. Both are HTTP 200 with an empty result, which is indistinguishable
        // from "nothing matched" — the same silent degradation FieldIndexing.No exhibits, and the
        // reason a missing Index(..., Search) call loses full-text search without any signal at all.
        (await ProbeAsync(q => q.Search(x => x.Exacted, "Bravo"))).Should().Be("0",
            "Exact stores the whole value as one term, so a single word matches nothing — silently");

        (await ProbeAsync(q => q.Search(x => x.Plain, "Bravo"))).Should().Be("0",
            "an unconfigured field is not analyzed either; only Search makes search() meaningful");
    }

    /// <summary>The converse: <c>Search</c> destroys the equality that <c>Exact</c> and plain keep.</summary>
    [Fact]
    public async Task Equality_survives_on_everything_except_an_analyzed_field()
    {
        (await ProbeAsync(q => q.Where(x => x.Plain == "Alpha Bravo"))).Should().Be("1");
        (await ProbeAsync(q => q.Where(x => x.Exacted == "Alpha Bravo"))).Should().Be("1");

        (await ProbeAsync(q => q.Where(x => x.Searched == "Alpha Bravo"))).Should().Be("0",
            "tokenization means the whole value is no longer a term — this is why a field used for " +
            "equality or row security must never be declared Search, and why sort companions exist");
    }

    /// <summary>
    /// The difference between <c>Exact</c> and no call at all: case sensitivity, nothing else.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is the measured reason a generated sort companion must <b>not</b> be declared
    /// <c>Exact</c>. It needs no field options to be sortable, and adding <c>Exact</c> would silently
    /// make ordering and filtering case-sensitive — "Zebra" before "apple", and a filter for
    /// "alpha bravo" matching nothing.
    /// </remarks>
    [Fact]
    public async Task Exact_differs_from_an_unconfigured_field_only_in_case_sensitivity()
    {
        (await ProbeAsync(q => q.Where(x => x.Plain == "alpha bravo"))).Should().Be("1",
            "RavenDB compares strings case-insensitively by default");

        (await ProbeAsync(q => q.Where(x => x.Exacted == "alpha bravo"))).Should().Be("0",
            "Exact is case-SENSITIVE — which is the point for an OIDC client id, and wrong for a sort key");
    }

    /// <summary>
    /// Ordering is compared by first element under asc and desc, not by row count — a count is equal
    /// either way and would pass vacuously against a field whose ordering is a no-op.
    /// </summary>
    [Fact]
    public async Task Ordering_works_without_any_field_options_and_is_a_no_op_once_analyzed()
    {
        static async Task<string> FirstAsync(IRavenQueryable<SemanticsProbe> q, bool ascending,
            Func<SemanticsProbe, string> select)
        {
            var ordered = ascending
                ? q.OrderBy(x => x.Plain)
                : q.OrderByDescending(x => x.Plain);
            return select((await ordered.ToListAsync())[0]);
        }

        using var session = Store.OpenAsyncSession();

        var ascFirst = await FirstAsync(
            session.Query<SemanticsProbe, SemanticsProbes_Overview>(), true, x => x.Plain);
        var descFirst = await FirstAsync(
            session.Query<SemanticsProbe, SemanticsProbes_Overview>(), false, x => x.Plain);

        ascFirst.Should().Be("Alpha Bravo", "a map-emitted field orders correctly with no Index(...) call");
        descFirst.Should().Be("Charlie Delta", "and the direction is honoured, so the ordering is real");
    }
}
