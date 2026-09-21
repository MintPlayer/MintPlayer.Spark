using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
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
    /// <remarks>
    /// ⚠️ <b><paramref name="allowedOwnerKeys"/> holds <c>provider:login</c> keys, and
    /// <c>GitHubProject.OwnerLogin</c> is a bare login</b> — so they are narrowed to the GitHub ones
    /// and unwrapped here, before the expression is built. Comparing the two directly matched
    /// nothing and hid every board from everyone; that shipped, and failed closed.
    /// <para>
    /// Unwrapping rather than qualifying, because this has to survive translation to RQL: a
    /// <see cref="ForgeOwner"/> constructed inside the expression cannot. Narrowing to the GitHub
    /// keys first is what keeps it honest — it is not the same as ignoring the provider, which
    /// would let a GitLab owner of the same name match a board they have nothing to do with.
    /// </para>
    /// <para>
    /// A board is a GitHub Projects V2 concept, so there is no second forge to serve here and
    /// <c>GitHubProject</c> deliberately carries no <c>Provider</c>. If that ever changes, this
    /// unwrapping is the thing that has to go first.
    /// </para>
    /// </remarks>
    public static Expression<Func<GitHubProject, bool>> Filter(string[] allowedOwnerKeys)
    {
        var logins = GitHubLoginsOf(allowedOwnerKeys);
        return project => project.Connection != RepositoryConnection.Disconnected
            && project.OwnerLogin.In(logins);
    }

    /// <summary>
    /// The GitHub logins within a set of <c>provider:login</c> keys, dropping every other forge's.
    /// </summary>
    private static string[] GitHubLoginsOf(string[] allowedOwnerKeys)
        => [.. allowedOwnerKeys
            .Select(key => ForgeOwner.TryParse(key, out var owner) ? owner : null)
            .Where(owner => owner is { Provider: EForgeProvider.GitHub })
            .Select(owner => owner!.Value.Login)];

    /// <summary>
    /// The same rule for one already-loaded board, so an imperative caller cannot drift from the
    /// query one. Kept deliberately adjacent to <see cref="Filter"/>: the two must always say the
    /// same thing, and the only way to keep that true is to make disagreement visible.
    /// </summary>
    public static bool IsVisible(GitHubProject project, string[] allowedOwnerKeys)
        => project.Connection != RepositoryConnection.Disconnected
            && GitHubLoginsOf(allowedOwnerKeys).Contains(project.OwnerLogin, StringComparer.OrdinalIgnoreCase);
}
