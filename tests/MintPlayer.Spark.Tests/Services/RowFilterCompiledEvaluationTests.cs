using System.Linq.Expressions;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// A row filter is written once and used twice: RavenDB translates the expression tree server-side,
/// and <c>RowSecurity.GetCompiledFilter</c> calls <see cref="Expression{TDelegate}.Compile"/> on the
/// <em>same</em> tree to evaluate it in memory when pushdown is not possible (a projection or an
/// author-supplied <c>IEnumerable</c>).
/// <para>
/// Nothing validates that a filter written with RavenDB-only LINQ survives that second use. This is
/// the test that does. <c>apps/CodeCoverage</c> ships exactly such a filter —
/// <c>CommitActions.GetRowFilterAsync</c> returns <c>c => c.Repository!.In(repoIds)</c> — and it is
/// safe today only because <c>Commit</c> has no index binding, so the compiled path is unreachable
/// for it. Add <c>[GenerateIndex]</c> or an <c>indexName</c> to that type and this becomes live.
/// </para>
/// <para>
/// <b>The dangerous outcome is not an exception.</b> A throw is loud and gets fixed. A compiled
/// <c>.In()</c> that silently returned <c>false</c> would deny every row on the fallback path only —
/// so the type would filter correctly until someone introduced a projection, and the symptom would
/// read as an indexing bug rather than a security one.
/// </para>
/// </summary>
public class RowFilterCompiledEvaluationTests
{
    private sealed class Doc
    {
        public string? Id { get; set; }
        public string? Owner { get; set; }
    }

    /// <summary>
    /// The positive case: a row the filter should keep must survive in-memory evaluation.
    /// If this fails by returning <c>false</c>, the fallback path silently denies everything.
    /// </summary>
    [Fact]
    public void A_RavenDB_In_filter_keeps_a_matching_row_when_compiled_in_memory()
    {
        string[] owners = ["alice", "carol"];
        Expression<Func<Doc, bool>> filter = d => d.Owner!.In(owners);

        var compiled = filter.Compile();

        compiled(new Doc { Owner = "alice" }).Should().BeTrue(
            "a filter written with RavenDB-only LINQ is compiled and run in memory on the projection " +
            "fallback path, so it must agree with what the server-side translation would have kept");
    }

    /// <summary>
    /// The negative case, asserted separately. A filter that returned <c>true</c> for everything
    /// would be the leaking twin of the failure above, and a single positive assertion cannot tell
    /// "works" apart from "always true".
    /// </summary>
    [Fact]
    public void A_RavenDB_In_filter_rejects_a_non_matching_row_when_compiled_in_memory()
    {
        string[] owners = ["alice", "carol"];
        Expression<Func<Doc, bool>> filter = d => d.Owner!.In(owners);

        var compiled = filter.Compile();

        compiled(new Doc { Owner = "bob" }).Should().BeFalse(
            "in-memory evaluation must not widen the filter either — a row the server would have " +
            "excluded must stay excluded");
    }

    /// <summary>
    /// The shape <c>CommitActions</c> actually uses: <c>.In()</c> over a reference property whose
    /// value is a document id, with the allow-list supplied at request time.
    /// </summary>
    [Fact]
    public void The_shape_used_in_production_survives_compilation()
    {
        IEnumerable<string> visible = new List<string> { "Repositories/1", "Repositories/2" };
        Expression<Func<Doc, bool>> filter = d => d.Owner!.In(visible);

        var compiled = filter.Compile();

        compiled(new Doc { Owner = "Repositories/2" }).Should().BeTrue();
        compiled(new Doc { Owner = "Repositories/9" }).Should().BeFalse();
    }

    /// <summary>
    /// An empty allow-list must deny, not admit. This is the case a row-scoped type hits whenever a
    /// caller can see nothing at all, and it is the one where a wrong answer is a full disclosure.
    /// </summary>
    [Fact]
    public void An_empty_allow_list_denies_every_row()
    {
        string[] owners = [];
        Expression<Func<Doc, bool>> filter = d => d.Owner!.In(owners);

        var compiled = filter.Compile();

        compiled(new Doc { Owner = "alice" }).Should().BeFalse(
            "a caller who may see nothing must be shown nothing — an empty allow-list that admitted " +
            "rows would be the worst possible failure of this path");
    }
}
