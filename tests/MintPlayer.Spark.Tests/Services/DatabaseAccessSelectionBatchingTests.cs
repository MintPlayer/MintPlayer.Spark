using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The cost of resolving a selection by id (#327 M2).
/// <para>
/// This is the test the change exists for. Correctness was never the problem — the per-row loop
/// resolved every row correctly — it was that a bulk action cost one full row-gated load per
/// selected row, and the mitigation lifted RavenDB's request ceiling rather than removing the
/// N+1. So the assertion here is about ROUND TRIPS, not about rows: bounded, and independent of
/// how many rows were selected.
/// </para>
/// <para>
/// Sibling of <c>RowSecurityProjectionBatchingTests</c>, which pins the same property for the
/// projection filter path.
/// </para>
/// </summary>
public class DatabaseAccessSelectionBatchingTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("5c1b22bb-22bb-22bb-22bb-5c1b22bb22bb");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private IDatabaseAccess _dbAccess = null!;
    private IAsyncDocumentSession _session = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<GuardedContext>(Store, [GuardedDocModel.For(DocTypeId)]);
        _dbAccess = _factory.GetService<IDatabaseAccess>();
        // Same instance the read path uses: both are scoped and both resolve off the root scope.
        _session = _factory.GetService<IAsyncDocumentSession>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SeedAsync(params GuardedDoc[] docs)
        => base.SeedAsync(async session =>
        {
            foreach (var d in docs) await session.StoreAsync(d);
        });

    private static GuardedDoc[] Docs(int count, bool visible = true) =>
        [.. Enumerable.Range(1, count).Select(i => new GuardedDoc
        {
            Id = $"docs/{i}",
            Name = $"doc {i}",
            IsVisible = visible,
        })];

    [Fact]
    public async Task A_selection_far_past_the_session_request_cap_is_resolved_in_a_bounded_number_of_round_trips()
    {
        // 50 rows: comfortably past RavenDB's stock 30-request ceiling, which the per-row loop used
        // to blow through at around five rows and which ExecuteCustomAction had to lift for the
        // whole handler as a result.
        var docs = Docs(50);
        await SeedAsync(docs);

        var before = _session.Advanced.NumberOfRequests;
        var rows = await _dbAccess.GetPersistentObjectsByIdAsync(DocTypeId, [.. docs.Select(d => d.Id!)]);
        var spent = _session.Advanced.NumberOfRequests - before;

        rows.Should().HaveCount(50);
        spent.Should().BeLessThanOrEqualTo(2,
            "the whole selection is one batched load plus at most one breadcrumb level — a per-row " +
            "loop would have spent 50");
    }

    [Fact]
    public async Task The_round_trip_cost_does_not_grow_with_the_selection_size()
    {
        // The property that matters is the shape of the curve, not the constant: a bounded number
        // that happens to be generous would still hide a reintroduced N+1.
        await SeedAsync(Docs(60));

        var beforeSmall = _session.Advanced.NumberOfRequests;
        await _dbAccess.GetPersistentObjectsByIdAsync(DocTypeId, [.. Enumerable.Range(1, 5).Select(i => $"docs/{i}")]);
        var small = _session.Advanced.NumberOfRequests - beforeSmall;

        var beforeLarge = _session.Advanced.NumberOfRequests;
        await _dbAccess.GetPersistentObjectsByIdAsync(DocTypeId, [.. Enumerable.Range(6, 50).Select(i => $"docs/{i}")]);
        var large = _session.Advanced.NumberOfRequests - beforeLarge;

        large.Should().Be(small, "ten times the rows must cost the same number of round trips");
    }

    [Fact]
    public async Task Rows_come_back_in_the_order_the_ids_were_given()
    {
        await SeedAsync(Docs(5));

        var rows = await _dbAccess.GetPersistentObjectsByIdAsync(
            DocTypeId, ["docs/4", "docs/1", "docs/5"]);

        rows.Select(r => r.Id).Should().ContainInOrder("docs/4", "docs/1", "docs/5");
    }

    [Fact]
    public async Task A_repeated_id_collapses_to_one_row()
    {
        await SeedAsync(Docs(3));

        var rows = await _dbAccess.GetPersistentObjectsByIdAsync(
            DocTypeId, ["docs/1", "docs/1", "docs/2"]);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Id).Should().ContainInOrder("docs/1", "docs/2");
    }

    [Fact]
    public async Task A_row_the_rule_refuses_is_omitted_not_nulled()
    {
        // Omitted, and for the same reason a single load returns null: "missing", "foreign
        // collection" and "not yours" must stay indistinguishable. The caller compares counts.
        await SeedAsync(
            new GuardedDoc { Id = "docs/visible", Name = "public", IsVisible = true },
            new GuardedDoc { Id = "docs/forbidden", Name = "secret", IsVisible = false });

        var rows = await _dbAccess.GetPersistentObjectsByIdAsync(
            DocTypeId, ["docs/visible", "docs/forbidden"]);

        rows.Should().HaveCount(1);
        rows[0].Id.Should().Be("docs/visible");
    }

    [Fact]
    public async Task An_id_naming_nothing_is_omitted_exactly_as_a_refused_one_is()
    {
        await SeedAsync(Docs(1));

        var rows = await _dbAccess.GetPersistentObjectsByIdAsync(
            DocTypeId, ["docs/1", "docs/does-not-exist"]);

        rows.Should().HaveCount(1);
        rows[0].Id.Should().Be("docs/1");
    }

    [Fact]
    public async Task The_singular_load_and_the_batch_agree_on_the_same_row()
    {
        // OnLoadAsync is defined as the one-id case of OnLoadManyAsync precisely so these cannot
        // drift; this pins that they actually produce the same page.
        await SeedAsync(Docs(1));

        var single = await _dbAccess.GetPersistentObjectAsync(DocTypeId, "docs/1");
        var batched = await _dbAccess.GetPersistentObjectsByIdAsync(DocTypeId, ["docs/1"]);

        batched.Should().HaveCount(1);
        single.Should().NotBeNull();
        batched[0].Id.Should().Be(single!.Id);
        batched[0].Breadcrumb.Should().Be(single.Breadcrumb);
        batched[0].Etag.Should().Be(single.Etag);
        batched[0].Attributes.Select(a => a.Name)
            .Should().BeEquivalentTo(single.Attributes.Select(a => a.Name));
    }
}
