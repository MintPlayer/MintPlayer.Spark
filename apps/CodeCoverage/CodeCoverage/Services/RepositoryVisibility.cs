using System.Linq.Expressions;
using CodeCoverage.Entities;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace CodeCoverage.Services;

/// <summary>
/// The single definition of "which repositories may this viewer see".
///
/// The rule is one line — public repositories are world-readable, private ones
/// need GitHub-granted access to the owner — and it was written three times: in
/// <c>BrowseController.ResolveVisibleRepository</c>, in
/// <see cref="SparkVisibility"/>'s id query, and in
/// <c>RepositoryActions.GetRowFilterAsync</c>. Two surfaces over the same
/// documents, two languages of expression, kept in step by a doc-comment.
/// The next visibility concept — an org allowlist, private-but-shared, an
/// unlisted state — has to land in both, and the generic /spark surface is the
/// one nobody remembers while making that change. So it lands here instead.
/// </summary>
public static class RepositoryVisibility
{
    /// <summary>
    /// The rule as a RavenDB-translatable predicate, for filtering a query.
    /// <para>
    /// <c>In()</c> rather than <c>Contains</c> is load-bearing, not style:
    /// Raven's LINQ provider fails twice on .NET 10 inside an <c>OrElse</c> — a
    /// <c>string[]</c> receiver binds to the untranslatable
    /// <c>MemoryExtensions.Contains</c>, and <c>List&lt;string&gt;.Contains</c>
    /// throws <c>TypedParameterExpression</c>. <c>In()</c> also has a real
    /// in-memory implementation, which Spark's compiled single-row checks rely
    /// on when they evaluate this same expression against one loaded document.
    /// </para>
    /// </summary>
    public static Expression<Func<Repository, bool>> Filter(string[] allowedOwners)
        => repository => !repository.IsPrivate || repository.OwnerLogin.In(allowedOwners);

    /// <summary>
    /// The same rule for one already-loaded repository, so an imperative caller
    /// cannot drift from the query one.
    /// </summary>
    public static bool IsVisible(Repository repository, string[] allowedOwners)
        => !repository.IsPrivate
            || allowedOwners.Contains(repository.OwnerLogin, StringComparer.OrdinalIgnoreCase);
}
