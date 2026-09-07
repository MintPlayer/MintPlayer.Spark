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

    /// <summary>
    /// Stamps each automation rule's <see cref="EventColumnMapping.Id"/> and refuses a board that
    /// maps one event twice.
    /// </summary>
    /// <remarks>
    /// <c>EventColumnMapping.Id</c> documents itself as "derived from the event type, because one
    /// board maps each event at most once" — and nothing derived it. Rules saved with
    /// <c>Id: ""</c>, verified in the database, so <b>every</b> rule on a board shared the same
    /// key. The inline collection editor identifies rows by it across saves, which makes two
    /// blank-keyed rows indistinguishable to the editor.
    /// <para>
    /// Derived here rather than in the entity's constructor or a property setter because the event
    /// type is not known until the client has filled the row in: the mapper materializes a rule
    /// from posted attribute values, so the earliest point at which an id can be correct is after
    /// mapping and before the write. That is exactly this hook.
    /// </para>
    /// <para>
    /// The duplicate check is part of the same fix, not a separate feature. Deriving the id from
    /// the event type is only sound while the event type is unique per board; without the check,
    /// two rules for one event would silently collapse to one key again, and the collision would be
    /// invisible in the database. It is also the honest answer for the user: two rules for the same
    /// event name different target columns, so the pair has no meaning to resolve — one of them
    /// would simply never fire, and which one would depend on ordering.
    /// </para>
    /// <para>
    /// Rules with no event type yet are left alone rather than rejected. The inline editor adds an
    /// empty row before anything is picked, and a board can legitimately be saved for an unrelated
    /// reason — toggling automation off — with a half-filled row present. Such a rule cannot match
    /// a delivery anyway: the recipient resolves rules by event key.
    /// </para>
    /// </remarks>
    public override Task OnBeforeSaveAsync(PersistentObject obj, GitHubProject entity)
    {
        var duplicates = entity.EventMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.EventType))
            .GroupBy(m => m.EventType, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"This board maps {string.Join(", ", duplicates)} more than once. Each event may "
                + "target only one column per board, so remove the duplicate rule or point the "
                + "existing one at a different column.");
        }

        foreach (var mapping in entity.EventMappings)
        {
            mapping.Id = mapping.EventType ?? string.Empty;
        }

        return Task.CompletedTask;
    }
}
