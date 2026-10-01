using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Contributions;

/// <summary>What one owner save asks of a declaration (built by <see cref="ContributionsInterceptor"/>).</summary>
internal sealed class OwnerSave
{
    public required SaveContext Context { get; init; }
    public required IAsyncDocumentSession Session { get; init; }

    /// <summary>The target's id; for a create, stores the target first so it has one (only when something is written).</summary>
    public required Func<Task<string>> TargetIdAsync { get; init; }

    /// <summary>The contributor: the caller's Spark user id (the one History stamps), or the system contributor.</summary>
    public required Func<string> UserId { get; init; }

    public required DateTimeOffset Now { get; init; }
    public required bool IsSystemContext { get; init; }
    public required IServiceProvider Services { get; init; }
    public ClaimsPrincipal? User { get; init; }

    /// <summary>Where the save's notices wait for the commit.</summary>
    public required ContributionRequestState State { get; init; }
}

/// <summary>A moderator's write over contributions (remove a version, revert), in the given session.</summary>
internal sealed class ModeratorWrite
{
    public required IAsyncDocumentSession Session { get; init; }

    /// <summary>Who hides (<c>DeletedBy</c>): the caller's id, or the system contributor.</summary>
    public required string UserId { get; init; }

    public required DateTimeOffset Now { get; init; }

    /// <summary>Asked before each document is hidden (empty for the system context).</summary>
    public required IReadOnlyList<ISatelliteWriteGuard> Guards { get; init; }

    public ClaimsPrincipal? User { get; init; }
}

/// <summary>The untyped face of <see cref="ContributionHandler{TTarget, TElement, TContribution, TCurrent}"/>.</summary>
internal interface IContributionHandler
{
    ContributionDescriptor Descriptor { get; }

    /// <summary>
    /// Fills the property from the current documents (one lazy request), and — with
    /// <paramref name="names"/> and <see cref="ContributionAttribution.Contributor"/> — the contributor
    /// names (one batched <c>ISparkUserNameResolver</c> call). With <paramref name="state"/>, records
    /// who wrote each row's version (<see cref="ContributionRequestState.RowContributors"/>).
    /// </summary>
    Task HydrateAsync(object target, IAsyncDocumentSession session, IServiceProvider? names, ContributionRequestState? state = null);

    /// <summary>A current document is being deleted (a moderator removes the whole version): hides every visible contribution of its slot.</summary>
    Task<IReadOnlyList<string>> OnCurrentDeletingAsync(object current, string id, ModeratorWrite write);

    /// <summary>Makes <paramref name="contributionId"/> current by hiding every newer visible contribution of its slot.</summary>
    Task<(string TargetId, IReadOnlyList<string> Affected)> RevertAsync(string contributionId, ModeratorWrite write);

    /// <summary>The target's contributions (every slot, hidden ones too), with names filled for the contributions query.</summary>
    Task<IReadOnlyList<IContribution>> ContributionsOfAsync(string targetId, IAsyncDocumentSession session, IServiceProvider services);

    /// <summary>The target id a current document belongs to (its id minus the property and slot segments).</summary>
    string? TargetIdOfCurrent(string currentId);

    /// <summary>Diffs the save by slot and writes the caller's own contributions and the current documents.</summary>
    Task OnOwnerSaveAsync(OwnerSave save);

    /// <summary>Deletes every contribution and current document of a target that is really deleted.</summary>
    Task OnOwnerDeletedAsync(string targetId, IAsyncDocumentSession session);

    /// <summary>A contribution document was saved directly (restore, moderator edit): recompute its slot.</summary>
    Task OnContributionSavedAsync(object contribution, object? before, IAsyncDocumentSession session);

    /// <summary>A contribution document is about to be deleted or hidden: recompute its slot without it.</summary>
    Task OnContributionDeletingAsync(object contribution, string id, IAsyncDocumentSession session);

    /// <summary>Recomputes every slot's current document of a target from its contributions.</summary>
    Task RebuildAsync(string targetId, IAsyncDocumentSession session);

    /// <summary>Every target id that has a contribution or a current document of this declaration (streamed).</summary>
    Task<IReadOnlyCollection<string>> TargetIdsAsync(IAsyncDocumentSession session, CancellationToken cancellationToken);
}

/// <summary>
/// The contributions runtime for one declaration (PRD T6). Every write goes into the session it is
/// given — the request session for a PO save or delete — so it commits atomically with that operation,
/// and every current document is written with a pinned change vector (S-C4).
/// </summary>
internal sealed class ContributionHandler<TTarget, TElement, TContribution, TCurrent> : IContributionHandler
    where TTarget : class
    where TElement : class
    where TContribution : class, IContribution
    where TCurrent : class, ICurrentContribution
{
    /// <summary>The explicit page size of every prefix load (RavenDB's default is 25, S-C6).</summary>
    internal const int PageSize = 1024;

    private readonly ContributionDescriptor<TTarget, TElement, TContribution, TCurrent> d;

    public ContributionHandler(ContributionDescriptor descriptor)
        => d = (ContributionDescriptor<TTarget, TElement, TContribution, TCurrent>)descriptor;

    public ContributionDescriptor Descriptor => d;

    private bool CountsContributions => (d.Attribution & ContributionAttribution.History) != 0;

    // ---- ids from slot keys (the same strings the generated GetIds produce) ------------------------

    private string SlotContributionPrefix(string targetId, string slotKey)
        => d.ContributionPrefix(targetId) + (slotKey.Length == 0 ? "" : slotKey + "/") + "User/";

    private string CurrentIdOf(string targetId, string slotKey)
        => slotKey.Length == 0 ? d.CurrentPrefix(targetId)[..^1] : d.CurrentPrefix(targetId) + slotKey;

    // ---- hydration ----------------------------------------------------------------------------------

    private bool ShowsContributor => (d.Attribution & ContributionAttribution.Contributor) != 0;

    public async Task HydrateAsync(object target, IAsyncDocumentSession session, IServiceProvider? names, ContributionRequestState? state = null)
    {
        var entity = (TTarget)target;
        var targetId = session.Advanced.GetDocumentId(entity);
        if (string.IsNullOrEmpty(targetId))
            return;

        var currents = await LoadCurrentsAsync(session, targetId, lazily: true);
        // Names are resolved at read time, never stored (PRD Q6): one batched call per entity.
        var resolved = names is not null && ShowsContributor && currents.Count > 0
            ? await ContributorNames.ResolveAsync(names, currents.Select(c => c.ContributorId))
            : null;
        d.SetRows(entity, [.. currents.Select(c => d.CreateRow(c, resolved?.NameOf(c.ContributorId)))]);

        if (state is not null)
        {
            var contributors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var current in currents)
                contributors[d.SlotKeyOfCurrent(current)] = current.ContributorId;
            state.RowContributors[(targetId, d.PropertyName)] = contributors;
        }
    }

    /// <summary>The target's current documents, in id order — one (lazy) request up to <see cref="PageSize"/>.</summary>
    private async Task<List<TCurrent>> LoadCurrentsAsync(IAsyncDocumentSession session, string targetId, bool lazily)
    {
        if (!d.IsCollection)
        {
            var id = CurrentIdOf(targetId, "");
            var one = lazily
                ? await session.Advanced.Lazily.LoadAsync<TCurrent>(id).Value
                : await session.LoadAsync<TCurrent>(id);
            return one is not null && InCollection(session, one) ? [one] : [];
        }

        return await LoadPrefixAsync<TCurrent>(session, d.CurrentPrefix(targetId), lazily);
    }

    /// <summary>
    /// Every document under <paramref name="prefix"/> (always ending in <c>/</c>) of <typeparamref name="T"/>'s
    /// collection, paged by <see cref="PageSize"/>. The first page is lazy when asked, so a hydration is one
    /// request; a second page only exists past <see cref="PageSize"/> documents.
    /// </summary>
    private static async Task<List<T>> LoadPrefixAsync<T>(IAsyncDocumentSession session, string prefix, bool lazily = false)
        where T : class
    {
        var result = new List<T>();
        string? startAfter = null;
        while (true)
        {
            IReadOnlyCollection<KeyValuePair<string, T>> page;
            if (lazily && startAfter is null)
                page = await session.Advanced.Lazily.LoadStartingWithAsync<T>(prefix, pageSize: PageSize).Value;
            else
            {
                var docs = await session.Advanced.LoadStartingWithAsync<T>(prefix, pageSize: PageSize, startAfter: startAfter);
                page = [.. docs.Where(x => x is not null).Select(x => new KeyValuePair<string, T>(session.Advanced.GetDocumentId(x), x))];
            }

            foreach (var (_, doc) in page)
                if (doc is not null && InCollection(session, doc))
                    result.Add(doc);

            if (page.Count < PageSize)
                return result;
            startAfter = page.Select(p => p.Key).Max(StringComparer.Ordinal);
        }
    }

    /// <summary>A prefix holds only this declaration's documents by construction; this guards a hand-made id.</summary>
    private static bool InCollection<T>(IAsyncDocumentSession session, T document) where T : class
    {
        var expected = session.Advanced.DocumentStore.Conventions.FindCollectionName(typeof(T));
        var metadata = session.Advanced.GetMetadataFor(document);
        return !metadata.TryGetValue(Raven.Client.Constants.Documents.Metadata.Collection, out string? collection)
               || collection is null
               || string.Equals(collection, expected, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsHidden(TContribution contribution) => d.IsSoftDeletable && SoftDeleteBridge.IsDeleted(contribution);

    // ---- the owner save -------------------------------------------------------------------------------

    public async Task OnOwnerSaveAsync(OwnerSave save)
    {
        var session = save.Session;
        var entity = (TTarget)save.Context.Entity!;
        var before = save.Context.Before is TTarget stored ? d.GetRows(stored) : [];
        var after = d.GetRows(entity);

        // Slot validity (T2) and one row per slot (S-C3), on what the caller posted.
        var afterByKey = new Dictionary<string, TElement>(StringComparer.Ordinal);
        var afterOrder = new List<string>();
        foreach (var row in after)
        {
            if (row is null)
                continue;
            if (d.FindInvalidSlot(row) is { } invalid)
                throw new SparkValidationException(
                    $"{d.PropertyName}: '{invalid}' must be 1 to 32 letters, digits or '-'.", d.PropertyName);
            var key = d.SlotKeyOf(row);
            if (!afterByKey.TryAdd(key, row))
                throw new SparkValidationException(
                    $"{d.PropertyName}: two rows share '{key}'. Each {string.Join(" and ", d.SlotNames)} holds one version.", d.PropertyName);
            afterOrder.Add(key);
        }

        var beforeByKey = new Dictionary<string, TElement>(StringComparer.Ordinal);
        foreach (var row in before)
            if (row is not null)
                beforeByKey.TryAdd(d.SlotKeyOf(row), row);

        // By value against the hydrated Before: an unchanged row writes nothing.
        var upserts = afterOrder.Where(k => !beforeByKey.TryGetValue(k, out var old) || !SameValues(old, afterByKey[k])).ToList();
        var removals = beforeByKey.Keys.Where(k => !afterByKey.ContainsKey(k)).ToList();
        if (upserts.Count == 0 && removals.Count == 0)
            return;

        // A create gets its id here (the target is stored early; a refusal below evicts it, F6).
        var targetId = await save.TargetIdAsync();

        // The app's domain rules (T7), per changed row. Not for the system context.
        if (!save.IsSystemContext)
        {
            var validators = save.Services.GetServices<IContributionValidator<TElement>>().ToList();
            if (validators.Count > 0)
            {
                foreach (var key in upserts)
                    foreach (var validator in validators)
                    {
                        var errors = await validator.ValidateAsync(targetId, afterByKey[key]);
                        if (errors is { Count: > 0 })
                            throw new SparkValidationException(
                                string.Join(" ", errors.Select(e => e.ErrorMessage.GetDefaultValue())),
                                errors[0].AttributeName is { Length: > 0 } name ? name : d.PropertyName);
                    }
            }
        }

        var userId = save.UserId();
        var updatedAt = save.Now.UtcDateTime;
        var guards = save.IsSystemContext ? [] : save.Services.GetServices<ISatelliteWriteGuard>().ToList();
        var finalCurrents = new Dictionary<string, TCurrent?>(StringComparer.Ordinal);

        foreach (var key in upserts)
        {
            var row = afterByKey[key];
            var id = d.ContributionId(targetId, row, userId);
            await GuardAsync(guards, id, targetId, save.User);

            var mine = await session.LoadAsync<TContribution>(id);
            if (mine is null)
            {
                mine = d.CreateContribution(targetId, row, userId, updatedAt);
                // Must not exist: a second tab of the same user creating the same slot conflicts (409).
                await session.StoreAsync(mine, string.Empty, id);
            }
            else
            {
                if (IsHidden(mine))
                {
                    // A moderator's hide is not undone by the author saving again; their own withdrawal is.
                    if (!SoftDeleteBridge.IsWithdrawnBy(mine, userId))
                        throw new SparkValidationException(
                            $"{d.PropertyName}: your version of '{key}' was hidden by a moderator and cannot be changed.", d.PropertyName);
                    SoftDeleteBridge.Revive(mine);
                }
                var changeVector = session.Advanced.GetChangeVectorFor(mine);
                d.CopyValues(row, mine);
                mine.UpdatedAt = updatedAt;
                mine.ContributorId = userId;
                mine.TargetId = targetId;
                await session.StoreAsync(mine, changeVector, id);
            }

            finalCurrents[key] = await RecomputeAsync(session, targetId, key, new() { [id] = mine }, preferred: mine);
        }

        // What the form must be told (Q3): (slot, message kind, the contributor shown now or null).
        var notices = new List<(string Key, bool Withdrawn, string? ShownContributor)>();

        foreach (var key in removals)
        {
            var id = d.ContributionId(targetId, beforeByKey[key], userId);
            var mine = await session.LoadAsync<TContribution>(id);
            // Only the caller's own, visible contribution is withdrawn (Q3). Removing a row that shows
            // someone else's version withdraws nothing, and that version stays current — and the form
            // says so, since the row comes back (also when a changed slot left the old version in place).
            if (mine is null || IsHidden(mine))
            {
                var shown = await session.LoadAsync<TCurrent>(CurrentIdOf(targetId, key));
                if (shown is not null && !string.Equals(shown.ContributorId, userId, StringComparison.Ordinal))
                    notices.Add((key, false, shown.ContributorId));
                continue;
            }
            await GuardAsync(guards, id, targetId, save.User);

            if (d.IsSoftDeletable)
            {
                var changeVector = session.Advanced.GetChangeVectorFor(mine);
                SoftDeleteBridge.Withdraw(mine, userId, save.Now);
                await session.StoreAsync(mine, changeVector, id);
            }
            else
                session.Delete(mine);

            var now = await RecomputeAsync(session, targetId, key, new() { [id] = null });
            finalCurrents[key] = now;
            notices.Add((key, true, now?.ContributorId));
        }

        if (notices.Count > 0 && !save.IsSystemContext)
            await AddNoticesAsync(save, notices);

        // The entity shows what is current now (the property is [JsonIgnore], so the target document is
        // not rewritten): the save response then lists a withdrawn row's previous version, if any.
        var rows = new List<TElement>();
        foreach (var key in afterOrder)
            rows.Add(finalCurrents.TryGetValue(key, out var current) && current is not null ? d.CreateRow(current, null) : afterByKey[key]);
        foreach (var key in removals)
        {
            if (!finalCurrents.TryGetValue(key, out var current))
                rows.Add(beforeByKey[key]);
            else if (current is not null)
                rows.Add(d.CreateRow(current, null));
        }
        d.SetRows(entity, rows);
    }

    /// <summary>
    /// The Q3 notices, queued until the commit: "your version was withdrawn; X's is shown now" (or none
    /// is left), and "X's version is not yours to remove: it stays". The other contributor is named only
    /// when the declaration shows contributors (<see cref="ContributionAttribution.Contributor"/>) and a
    /// name resolves; otherwise the text is generic.
    /// </summary>
    private async Task AddNoticesAsync(OwnerSave save, List<(string Key, bool Withdrawn, string? ShownContributor)> notices)
    {
        var names = ShowsContributor
            ? await ContributorNames.ResolveAsync(save.Services, notices.Select(n => n.ShownContributor))
            : null;
        foreach (var (key, withdrawn, shown) in notices)
        {
            var slot = key.Length == 0 ? d.PropertyName : key;
            var name = names?.NameOf(shown);
            string message;
            if (withdrawn)
            {
                message = shown is null
                    ? ContributionMessages.Format(save.Services, ContributionMessages.WithdrawnNoneLeft, slot)
                    : name is null
                        ? ContributionMessages.Format(save.Services, ContributionMessages.WithdrawnNowShowingAnother, slot)
                        : ContributionMessages.Format(save.Services, ContributionMessages.WithdrawnNowShowing, slot, name);
                save.State.Notices.Add((message, Abstractions.ClientOperations.NotificationKind.Info));
            }
            else
            {
                message = name is null
                    ? ContributionMessages.Format(save.Services, ContributionMessages.NotYoursStaysAnother, slot)
                    : ContributionMessages.Format(save.Services, ContributionMessages.NotYoursStays, slot, name);
                save.State.Notices.Add((message, Abstractions.ClientOperations.NotificationKind.Warning));
            }
        }
    }

    private async Task GuardAsync(IReadOnlyList<ISatelliteWriteGuard> guards, string documentId, string targetId, ClaimsPrincipal? user)
    {
        if (guards.Count == 0)
            return;
        var context = new SatelliteWriteContext
        {
            DocumentType = typeof(TContribution),
            DocumentId = documentId,
            TargetType = typeof(TTarget),
            TargetId = targetId,
            User = user,
        };
        foreach (var guard in guards)
            await guard.EnsureMayWriteAsync(context);
    }

    // ---- recompute ------------------------------------------------------------------------------------

    /// <summary>
    /// Makes the slot's current document the latest visible contribution (max <c>UpdatedAt</c>), or
    /// deletes it when none is left. <paramref name="overrides"/> replaces what the database holds for
    /// those ids (a contribution written or withdrawn in this session; <see langword="null"/> = gone).
    /// With <paramref name="preferred"/> (the caller's own upsert) that one becomes current, and the
    /// slot is only read when the declaration counts contributions.
    /// </summary>
    private async Task<TCurrent?> RecomputeAsync(IAsyncDocumentSession session, string targetId, string slotKey,
        Dictionary<string, TContribution?> overrides, TContribution? preferred = null)
    {
        TContribution? winner;
        var count = 0;
        if (preferred is not null && !CountsContributions)
            winner = preferred;
        else
        {
            var byId = new Dictionary<string, TContribution>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in await LoadPrefixAsync<TContribution>(session, SlotContributionPrefix(targetId, slotKey)))
                byId[session.Advanced.GetDocumentId(c) ?? ""] = c;
            foreach (var (id, c) in overrides)
            {
                if (c is null)
                    byId.Remove(id);
                else
                    byId[id] = c;
            }

            var visible = byId.Where(kv => !IsHidden(kv.Value)).ToList();
            count = visible.Count;
            winner = preferred ?? visible
                .OrderByDescending(kv => kv.Value.UpdatedAt)
                .ThenByDescending(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value)
                .FirstOrDefault();
        }

        return await WriteCurrentAsync(session, CurrentIdOf(targetId, slotKey), winner, count);
    }

    /// <summary>
    /// Writes (or deletes) one current document with a pinned change vector: update → the version this
    /// session loaded, create → <c>""</c> (must not exist), delete → the loaded version (S-C4).
    /// </summary>
    private async Task<TCurrent?> WriteCurrentAsync(IAsyncDocumentSession session, string id, TContribution? winner, int count)
    {
        var existing = await session.LoadAsync<TCurrent>(id);
        if (winner is null)
        {
            if (existing is not null)
                session.Delete(id, session.Advanced.GetChangeVectorFor(existing));
            return null;
        }

        var desired = d.CreateCurrent(winner, count);
        if (existing is null)
        {
            await session.StoreAsync(desired, string.Empty, id);
            return desired;
        }

        if (SameDocument(existing, desired))
            return existing;

        // A fresh instance under the loaded change vector: the tracked one is swapped out, never mutated,
        // so a refused save (F6) evicts the new one and leaves nothing changed behind.
        var changeVector = session.Advanced.GetChangeVectorFor(existing);
        session.Advanced.Evict(existing);
        await session.StoreAsync(desired, changeVector, id);
        return desired;
    }

    // ---- direct writes of a contribution document ----------------------------------------------------

    public async Task OnContributionSavedAsync(object contribution, object? before, IAsyncDocumentSession session)
    {
        var c = (TContribution)contribution;
        var id = session.Advanced.GetDocumentId(c);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(c.TargetId))
            return;

        // An edit that moved the document's slot fields (its id cannot move) recomputes the old slot too.
        if (before is TContribution old && d.SlotKeyOfContribution(old) is var oldKey && oldKey != d.SlotKeyOfContribution(c))
            await RecomputeAsync(session, old.TargetId, oldKey, new() { [id] = null });

        await RecomputeAsync(session, c.TargetId, d.SlotKeyOfContribution(c), new() { [id] = c });
    }

    public async Task OnContributionDeletingAsync(object contribution, string id, IAsyncDocumentSession session)
    {
        var c = (TContribution)contribution;
        if (string.IsNullOrEmpty(c.TargetId))
            return;
        // Treated as hidden whether SoftDelete replaced the delete or not: the current document commits
        // with the delete, in this session (S-C5: before-delete, never after the commit).
        await RecomputeAsync(session, c.TargetId, d.SlotKeyOfContribution(c), new() { [id] = null });
    }

    // ---- moderator writes (PRD Q4) ------------------------------------------------------------------

    private int SuffixSegments => 1 + (d.IsCollection ? d.SlotNames.Count : 0);

    public string? TargetIdOfCurrent(string currentId) => TargetOf(currentId, SuffixSegments);

    /// <summary>
    /// "Remove a whole version" (Delete on the current type): every visible contribution of the slot is
    /// hidden (reason <c>version-removed</c>; deleted when the type is not soft-deletable), in the
    /// delete's own session and commit, so the slot has no current version afterwards — the base delete
    /// then removes the current document itself. Hidden contributions stay restorable one by one.
    /// </summary>
    public async Task<IReadOnlyList<string>> OnCurrentDeletingAsync(object current, string id, ModeratorWrite write)
    {
        var c = (TCurrent)current;
        var targetId = TargetIdOfCurrent(id);
        if (targetId is null)
            return [];
        var session = write.Session;
        var slotKey = d.SlotKeyOfCurrent(c);

        var affected = new List<string>();
        foreach (var contribution in await LoadPrefixAsync<TContribution>(session, SlotContributionPrefix(targetId, slotKey)))
        {
            if (IsHidden(contribution))
                continue;
            var contributionId = session.Advanced.GetDocumentId(contribution);
            await GuardAsync(write.Guards, contributionId, targetId, write.User);
            await HideAsync(session, contribution, contributionId, write, SoftDeleteBridge.VersionRemovedReason);
            affected.Add(contributionId);
        }
        affected.Sort(StringComparer.Ordinal);
        return affected;
    }

    /// <summary>
    /// <c>RevertContribution</c>: makes <paramref name="contributionId"/> its slot's current version by
    /// hiding every newer visible contribution of the slot (reason <c>reverted</c>), never rewriting or
    /// re-attributing any text, then recomputes the current document — all in <paramref name="write"/>'s
    /// session, so it commits atomically (pinned change vectors: a concurrent write is a 409).
    /// </summary>
    /// <remarks>
    /// Refused (400): a hidden target — reverting to a hidden version means restoring it first
    /// (<c>Restore</c>, a separate right, and the restored one is then the latest by its own date only
    /// if nothing newer is visible); a declaration whose contributions are not soft-deletable (a revert
    /// would have to destroy other users' versions).
    /// </remarks>
    public async Task<(string TargetId, IReadOnlyList<string> Affected)> RevertAsync(string contributionId, ModeratorWrite write)
    {
        var session = write.Session;
        var target = await session.LoadAsync<TContribution>(contributionId)
            ?? throw new SparkValidationException("That contribution does not exist.");
        if (!d.IsSoftDeletable)
            throw new SparkValidationException(
                $"{typeof(TContribution).Name} is not soft-deletable, so a revert cannot hide the newer versions. Reference MintPlayer.Spark.SoftDelete.");
        if (IsHidden(target))
            throw new SparkValidationException("This version is hidden. Restore it before reverting to it.");

        var targetId = target.TargetId;
        var slotKey = d.SlotKeyOfContribution(target);
        var targetRank = (target.UpdatedAt, contributionId);

        var overrides = new Dictionary<string, TContribution?>(StringComparer.OrdinalIgnoreCase);
        var affected = new List<string>();
        foreach (var contribution in await LoadPrefixAsync<TContribution>(session, SlotContributionPrefix(targetId, slotKey)))
        {
            var id = session.Advanced.GetDocumentId(contribution);
            if (IsHidden(contribution) || string.Equals(id, contributionId, StringComparison.OrdinalIgnoreCase))
                continue;
            // "Newer" in the order recompute picks by: UpdatedAt, then id (ordinal) on a tie.
            if (Compare((contribution.UpdatedAt, id), targetRank) <= 0)
                continue;
            await GuardAsync(write.Guards, id, targetId, write.User);
            await HideAsync(session, contribution, id, write, SoftDeleteBridge.RevertedReason);
            overrides[id] = contribution;
            affected.Add(id);
        }

        var current = await RecomputeAsync(session, targetId, slotKey, overrides);
        if (current is null || !string.Equals(current.ContributionId, contributionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Reverting to {contributionId} left {current?.ContributionId ?? "no version"} current.");

        affected.Sort(StringComparer.Ordinal);
        return (targetId, affected);

        static int Compare((DateTime At, string Id) a, (DateTime At, string Id) b)
        {
            var byDate = DateTime.SpecifyKind(a.At, DateTimeKind.Utc).CompareTo(DateTime.SpecifyKind(b.At, DateTimeKind.Utc));
            return byDate != 0 ? byDate : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }
    }

    private async Task HideAsync(IAsyncDocumentSession session, TContribution contribution, string id, ModeratorWrite write, string reason)
    {
        if (d.IsSoftDeletable)
        {
            var changeVector = session.Advanced.GetChangeVectorFor(contribution);
            SoftDeleteBridge.Hide(contribution, write.UserId, write.Now, reason);
            await session.StoreAsync(contribution, changeVector, id);
        }
        else
            session.Delete(id, session.Advanced.GetChangeVectorFor(contribution));
    }

    public async Task<IReadOnlyList<IContribution>> ContributionsOfAsync(string targetId, IAsyncDocumentSession session, IServiceProvider services)
    {
        var contributions = await LoadPrefixAsync<TContribution>(session, d.ContributionPrefix(targetId));
        var names = await ContributorNames.ResolveAsync(services, contributions.Select(c => c.ContributorId));
        foreach (var c in contributions)
            c.ContributorName = names.NameOf(c.ContributorId);
        return contributions;
    }

    // ---- the owner's delete ---------------------------------------------------------------------------

    public async Task OnOwnerDeletedAsync(string targetId, IAsyncDocumentSession session)
    {
        foreach (var c in await LoadPrefixAsync<TContribution>(session, d.ContributionPrefix(targetId)))
            session.Delete(c);
        foreach (var current in await LoadCurrentsAsync(session, targetId, lazily: false))
            session.Delete(current);
    }

    // ---- rebuild --------------------------------------------------------------------------------------

    public async Task RebuildAsync(string targetId, IAsyncDocumentSession session)
    {
        var bySlot = (await LoadPrefixAsync<TContribution>(session, d.ContributionPrefix(targetId)))
            .GroupBy(c => d.SlotKeyOfContribution(c), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var currents = await LoadCurrentsAsync(session, targetId, lazily: false);

        foreach (var (slotKey, contributions) in bySlot)
        {
            var visible = contributions.Where(c => !IsHidden(c)).ToList();
            var winner = visible
                .OrderByDescending(c => c.UpdatedAt)
                .ThenByDescending(c => session.Advanced.GetDocumentId(c), StringComparer.Ordinal)
                .FirstOrDefault();
            await WriteCurrentAsync(session, CurrentIdOf(targetId, slotKey), winner, visible.Count);
        }

        // A current document no contribution backs, or one stored under an id its slot does not give.
        foreach (var current in currents)
        {
            var id = session.Advanced.GetDocumentId(current);
            if (id is null || !session.Advanced.IsLoaded(id))
                continue;
            var slotKey = d.SlotKeyOfCurrent(current);
            if (!bySlot.ContainsKey(slotKey) || !string.Equals(id, CurrentIdOf(targetId, slotKey), StringComparison.OrdinalIgnoreCase))
                session.Delete(id, session.Advanced.GetChangeVectorFor(current));
        }
    }

    public async Task<IReadOnlyCollection<string>> TargetIdsAsync(IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collection queries without a filter are served from the collection, not an index.
        await using (var contributions = await session.Advanced.StreamAsync(session.Query<TContribution>(), cancellationToken))
            while (await contributions.MoveNextAsync())
                if (contributions.Current.Document?.TargetId is { Length: > 0 } targetId)
                    ids.Add(targetId);

        // The current id is {targetId}/{Property}[/{slot}…]: drop the last 1 + slot-count segments.
        var suffixSegments = 1 + (d.IsCollection ? d.SlotNames.Count : 0);
        await using (var currents = await session.Advanced.StreamAsync(session.Query<TCurrent>(), cancellationToken))
            while (await currents.MoveNextAsync())
                if (TargetOf(currents.Current.Id, suffixSegments) is { } targetId)
                    ids.Add(targetId);

        return ids;
    }

    private static string? TargetOf(string? currentId, int suffixSegments)
    {
        if (string.IsNullOrEmpty(currentId))
            return null;
        var end = currentId.Length;
        for (var i = 0; i < suffixSegments; i++)
        {
            end = currentId.LastIndexOf('/', end - 1);
            if (end <= 0)
                return null;
        }
        return currentId[..end];
    }

    // ---- value comparison ---------------------------------------------------------------------------

    /// <summary>Whether two rows of the same slot hold the same values (the generated mapping, compared as JSON).</summary>
    private bool SameValues(TElement a, TElement b)
        => JsonSerializer.Serialize(d.CreateContribution("", a, "", default)) == JsonSerializer.Serialize(d.CreateContribution("", b, "", default));

    /// <summary>Whether a stored current document already says what a recompute wants (no write then).</summary>
    private static bool SameDocument(TCurrent stored, TCurrent desired)
    {
        if (DateTime.SpecifyKind(stored.UpdatedAt, DateTimeKind.Utc) != DateTime.SpecifyKind(desired.UpdatedAt, DateTimeKind.Utc))
            return false;
        var left = JsonSerializer.SerializeToNode(stored)!.AsObject();
        var right = JsonSerializer.SerializeToNode(desired)!.AsObject();
        left.Remove(nameof(ICurrentContribution.UpdatedAt));
        right.Remove(nameof(ICurrentContribution.UpdatedAt));
        left.Remove("Id");
        right.Remove("Id");
        return JsonNode.DeepEquals(left, right);
    }
}
