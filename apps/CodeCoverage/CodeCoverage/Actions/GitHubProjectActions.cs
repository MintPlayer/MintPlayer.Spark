using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// Row security for project boards on the generic /spark surface.
/// <para>
/// The class name keeps its generic argument — <c>DefaultPersistentObjectActions&lt;GitHubProject&gt;</c>
/// — because <c>ActionsResolver</c> matches actions to types through it and throws without it.
/// </para>
/// <para>
/// Unlike <see cref="RepositoryActions"/> this needs no <c>Query</c>/<c>Read</c> split. That split
/// exists so a disconnected repository stays resolvable by name for published badges and report
/// links; a board has no published surface, so one rule covers both. And unlike repositories there
/// is no anonymous tier at all: an empty owner list yields no boards rather than "the public ones".
/// </para>
/// </summary>
public partial class GitHubProjectActions : DefaultPersistentObjectActions<GitHubProject>
{
    [Inject] private readonly ISparkVisibility visibility;

    public override async Task<Expression<Func<GitHubProject, bool>>?> GetRowFilterAsync(string action)
    {
        // Awaiting I/O here is safe by contract: the framework invokes this hook at most once per
        // (entity type, action) per request and caches the result — bounded by the model, never by
        // row count or page size. On a stream the cache refreshes on the periodic re-authorization
        // tick, so the filter is at most that stale. See docs/guide-row-security.md.
        //
        // The corollary is the constraint: because the result is cached per request, the filter has
        // to be a pure function of request-scoped state. Per-row rules belong in IsAllowedAsync,
        // which is genuinely per-row and deliberately not memoized.
        var owners = await visibility.GetAllowedOwnersAsync();

        // Applies to writes as well as reads, deliberately. Boards are mutable through this surface
        // — enabling automation and editing rules is the whole point — so unlike RepositoryActions
        // the WITH CHECK path here is reachable, and this same predicate is what stops a caller
        // editing a board belonging to an owner they do not manage. Returning a read-only filter
        // and assuming writes are denied at the type level would be wrong for this type.
        return GitHubProjectVisibility.Filter(owners);
    }
}
