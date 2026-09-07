using System.Linq.Expressions;
using CodeCoverage.Entities;
using Raven.Client.Documents.Linq;

namespace CodeCoverage.Services;

/// <summary>
/// The single definition of "which project boards may this viewer see".
/// <para>
/// Modelled on <see cref="RepositoryVisibility"/>, and it inherits both of that type's load-bearing
/// spellings — see the remarks on each member. It differs from it in one deliberate way:
/// <b>there is no public tier.</b> A repository can be world-readable because its coverage badge is
/// embedded in a README and its report links sit in pull-request comments, so hiding it would break
/// published URLs. A board has no published surface: nothing links to it, nothing embeds it, and it
/// carries the owner's automation rules. So visibility is owner-management, full stop, and there is
/// no <c>!IsPrivate</c> escape hatch to get wrong.
/// </para>
/// <para>
/// That also means this type needs only <em>one</em> rule where <see cref="RepositoryVisibility"/>
/// needs two. Its split between <c>Filter</c> ("may this viewer see it") and <c>ListingFilter</c>
/// ("should we still advertise it") exists because a disconnected repository must stay reachable by
/// name; a disconnected board has no by-name surface to keep working, so the narrow rule is the only
/// rule.
/// </para>
/// </summary>
public static class GitHubProjectVisibility
{
    /// <summary>
    /// The rule as a RavenDB-translatable predicate, for filtering a query.
    /// <para>
    /// <c>In()</c> rather than <c>Contains</c> is load-bearing, not style: Raven's LINQ provider
    /// fails twice on .NET 10 — a <c>string[]</c> receiver binds to the untranslatable
    /// <c>MemoryExtensions.Contains</c>, and <c>List&lt;string&gt;.Contains</c> throws
    /// <c>TypedParameterExpression</c>. <c>In()</c> also has a real in-memory implementation, which
    /// Spark's compiled single-row checks rely on when they evaluate this same expression against
    /// one loaded document.
    /// </para>
    /// <para>
    /// The connection clause is written as <c>!= Disconnected</c> and NOT as <c>== Connected</c>.
    /// An absent JSON field does not satisfy an equality in RavenDB, so equality would hide every
    /// board document written before the field existed. Negation matches the absent field, so those
    /// documents read as connected — which is what they are.
    /// </para>
    /// </summary>
    public static Expression<Func<GitHubProject, bool>> Filter(string[] allowedOwners)
        => project => project.Connection != RepositoryConnection.Disconnected
            && project.OwnerLogin.In(allowedOwners);

    /// <summary>
    /// The same rule for one already-loaded board, so an imperative caller cannot drift from the
    /// query one. Kept deliberately adjacent to <see cref="Filter"/>: the two must always say the
    /// same thing, and the only way to keep that true is to make disagreement visible.
    /// </summary>
    public static bool IsVisible(GitHubProject project, string[] allowedOwners)
        => project.Connection != RepositoryConnection.Disconnected
            && allowedOwners.Contains(project.OwnerLogin, StringComparer.OrdinalIgnoreCase);
}
