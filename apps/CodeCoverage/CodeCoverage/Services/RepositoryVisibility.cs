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

    /// <summary>
    /// The rule for <em>enumerating</em> repositories, as opposed to resolving one by name.
    /// <para>
    /// There are deliberately two rules here, and the difference is the whole point rather than an
    /// oversight to be tidied away. <see cref="Filter"/> answers "may this viewer see this
    /// repository", and a disconnected repository is still perfectly visible: its badge is still
    /// embedded in a README somewhere and its report links are still sitting in pull-request
    /// comments, and breaking those would be a worse failure than the one this state exists to fix.
    /// This one answers the narrower question "should we still be <em>advertising</em> it", which is
    /// what a grid, an account page or a repository count is really asking — and the answer for a
    /// repository we can no longer reach is no, except to the owner who may want to delete it.
    /// </para>
    /// <para>
    /// Collapsing the two back into one is therefore not a simplification: routing the by-name
    /// lookups through this rule silently 404s every published badge the moment a repository is
    /// transferred away, and routing the listings through <see cref="Filter"/> restores the bug
    /// where an app advertises repositories it lost access to months ago.
    /// </para>
    /// <para>
    /// <c>In()</c> rather than <c>Contains</c> for the reason given on <see cref="Filter"/>. The
    /// connection clause is written as <c>!= Disconnected</c> and NOT as <c>== Connected</c>, which
    /// is load-bearing: every repository document written before this field existed has no
    /// <c>Connection</c> property at all, and an absent field does not satisfy an equality in
    /// RavenDB. Testing for equality here would hide every pre-existing repository from every
    /// anonymous visitor the moment this deploys, until something happened to rewrite each document
    /// — which is the same class of bug as this whole feature exists to fix, arriving from the
    /// other direction. Negation matches the absent field, so old documents read as connected,
    /// which is what they are.
    /// </para>
    /// </summary>
    public static Expression<Func<Repository, bool>> ListingFilter(string[] allowedOwners)
        => repository => (repository.Connection != RepositoryConnection.Disconnected && !repository.IsPrivate)
            || repository.OwnerLogin.In(allowedOwners);

    /// <summary>
    /// <see cref="ListingFilter"/> for one already-loaded repository. Same negation, for the same
    /// reason — a document that arrived without the field materialises as the enum's default, and
    /// the two forms must not disagree about it.
    /// </summary>
    public static bool IsListed(Repository repository, string[] allowedOwners)
        => (repository.Connection != RepositoryConnection.Disconnected && !repository.IsPrivate)
            || allowedOwners.Contains(repository.OwnerLogin, StringComparer.OrdinalIgnoreCase);
}
