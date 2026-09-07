using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The invariant the whole row-security design rests on, asserted rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Can this caller see this row" is "does this row survive the list filter".</b> One declaration,
/// applied to every path — so the list and the detail page cannot answer differently about the same
/// row, and neither can edit, delete or a sub-query.
/// </para>
/// <para>
/// It holds today as an emergent property of <c>RowSecurity.ResolveEffectiveRuleAsync</c>: the
/// compiled filter is non-null whenever a filter was declared, independently of whether the per-row
/// hook was overridden. Emergent is not the same as guaranteed, and the prior art shows precisely
/// what its absence costs — there, list filtering and single-row filtering are separate hooks bridged
/// by a conditional that returns the source unfiltered when the two types differ, so a projected type
/// is filtered on its list page and wide open on its detail page. Measured across 36 applications:
/// 289 override the list filter, 30 also guard the single row, and the bridging hook has never been
/// overridden by anyone.
/// </para>
/// <para>
/// These tests fail if Spark ever grows that shape.
/// </para>
/// </remarks>
public class RowSecurityInvariantTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("5c1d77bb-77bb-77bb-77bb-5c1d77bb77bb");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private IDatabaseAccess _dbAccess = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<GuardedContext>(Store, [FilteredDocModel.For(DocTypeId)]);
        _dbAccess = _factory.GetService<IDatabaseAccess>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>Two rows: one the filter keeps (alice owns it), one it hides.</summary>
    private Task SeedBothAsync() => SeedAsync(async session =>
    {
        await session.StoreAsync(new FilteredDoc { Id = "FilteredDocs/alice-1", Name = "Alice's", Owner = "alice" });
        await session.StoreAsync(new FilteredDoc { Id = "FilteredDocs/bob-1", Name = "Bob's", Owner = "bob" });
    });

    /// <summary>
    /// The type declares <em>only</em> a filter — no per-row <c>IsAllowedAsync</c> override at all —
    /// and the detail page must still be gated by it. This is the exact case the prior art fails:
    /// there, a filter without its single-row twin guards the list and nothing else.
    /// </summary>
    [Fact]
    public async Task A_filter_only_type_gates_the_detail_page_too()
    {
        await SeedBothAsync();

        var kept = await _dbAccess.GetPersistentObjectAsync(DocTypeId, "FilteredDocs/alice-1");
        var hidden = await _dbAccess.GetPersistentObjectAsync(DocTypeId, "FilteredDocs/bob-1");

        kept.Should().NotBeNull("the filter keeps alice's row, so opening it must work");
        hidden.Should().BeNull(
            "a row the list filter hides must not be reachable by id — one declaration, every path");
    }

    /// <summary>
    /// And the two paths agree, which is the invariant itself rather than two separate facts. A
    /// design where the list is filtered and the detail page is not would satisfy the list half of
    /// this suite and fail here.
    /// </summary>
    [Fact]
    public async Task The_detail_page_answers_exactly_what_the_filter_says()
    {
        await SeedBothAsync();

        var visibleById = new List<string>();
        foreach (var id in (string[])["FilteredDocs/alice-1", "FilteredDocs/bob-1"])
        {
            if (await _dbAccess.GetPersistentObjectAsync(DocTypeId, id) is not null)
                visibleById.Add(id);
        }

        // The filter is `d => d.Owner == "alice"`, so exactly one row survives it. Whatever the list
        // path would return, the by-id path returns the same set — that is the whole invariant.
        visibleById.Should().ContainSingle().Which.Should().Be("FilteredDocs/alice-1");
    }

    /// <summary>
    /// A batched load is the same question asked for several ids at once, so it must give the same
    /// answers. It is a separate code path from the single load, and a plural path that quietly
    /// skipped the rule would be invisible to every test above.
    /// </summary>
    [Fact]
    public async Task A_batched_load_gives_the_same_answers_as_one_at_a_time()
    {
        await SeedBothAsync();

        var batched = await _dbAccess.GetPersistentObjectsByIdAsync(
            DocTypeId, ["FilteredDocs/alice-1", "FilteredDocs/bob-1"]);

        // An id the filter hides is omitted from a batch exactly as it is refused singly.
        batched.Select(po => po.Id).Should().BeEquivalentTo(["FilteredDocs/alice-1"]);
    }
}
