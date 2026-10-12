using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace CodeCoverage.CommitGraph;

/// <summary>
/// Everything that creates, refreshes, evicts or deletes a commit-graph mirror (#504, PRD §4.2,
/// D10, D19, D20). The sidecar does the git work; this decides when, with which credential and
/// quota, and keeps <see cref="RepositoryGraphState"/> honest.
/// </summary>
public interface ICommitGraphMirrors
{
    /// <summary>
    /// Clones or fetches one mirror. Without <paramref name="allowClone"/> it only refreshes a
    /// mirror that exists (D10), and asks the sidecar for <c>fetch-only</c> so a mirror the sidecar
    /// lost is not re-cloned either (D40). Records every outcome on the state; never throws for one.
    /// </summary>
    Task FetchAsync(string repositoryId, bool allowClone, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the mirror, then its state (in that order: the invariant on
    /// <see cref="RepositoryGraphState"/>). Throws when the sidecar cannot be reached, so the message
    /// that asked is retried rather than forgotten.
    /// </summary>
    Task DeleteAsync(string repositoryId, string reason, bool eviction = false, CancellationToken cancellationToken = default);

    /// <summary>The daily sweep: evicts mirrors not viewed for <c>EvictAfterDays</c>, and deletes those whose repository is gone or disconnected.</summary>
    Task<int> EvictIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>The hourly safety net: queues a refresh of every mirror whose last fetch is over an hour old.</summary>
    Task<int> QueueRefreshesAsync(CancellationToken cancellationToken = default);
}

/// <remarks>
/// <para>
/// <b>Short sessions of its own, with optimistic concurrency.</b> A clone can take the whole quota
/// (minutes) and meanwhile the endpoint stamps <c>LastViewedAtUtc</c> and a disconnect may delete the
/// state. A tracked document saved at the end would silently overwrite the one and resurrect the
/// other, so every state write reloads, mutates and saves under a change-vector check, and a state
/// that disappeared mid-fetch takes its fresh mirror with it.
/// </para>
/// <para>
/// <b>The budget is soft (D20).</b> A clone's size is unknown until it finishes, so the app trims
/// before a clone (no room at all) and again after it (back under), least-recently-viewed first.
/// The host free-space floor is the sidecar's to enforce.
/// </para>
/// </remarks>
[Register(typeof(ICommitGraphMirrors), ServiceLifetime.Scoped)]
public partial class CommitGraphMirrors : ICommitGraphMirrors
{
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ICommitGraphSidecar sidecar;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IOptions<CommitGraphOptions> options;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<CommitGraphMirrors> logger;

    /// <summary>Margin on top of the quota timeout for the HTTP call itself (D19).</summary>
    private static readonly TimeSpan FetchMargin = TimeSpan.FromSeconds(60);

    /// <summary>Bound on the states listed by prefix; the budget keeps the real number far below it.</summary>
    private const int MaxStates = 8192;

    public async Task FetchAsync(string repositoryId, bool allowClone, CancellationToken cancellationToken = default)
    {
        if (!sidecar.IsConfigured || !CommitGraphSidecarClient.TryPath(repositoryId, out _)) return;
        var stateId = RepositoryGraphState.ForRepository(repositoryId);

        Repository? repository;
        RepositoryGraphState? state;
        using (var session = store.OpenAsyncSession())
        {
            state = await session.LoadAsync<RepositoryGraphState>(stateId, cancellationToken);
            repository = await session.LoadAsync<Repository>(repositoryId, cancellationToken);
        }

        // No state, no mirror: nothing asked for one, or it was deleted since this was queued.
        if (state is null) return;
        if (!state.HasMirror && !allowClone)
        {
            await ClearSyncingAsync(stateId, cancellationToken);
            return;
        }

        // Access lost while this waited in the queue: the mirror goes with it (PRD §5.2).
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected)
        {
            await DeleteAsync(repositoryId, "disconnected", cancellationToken: cancellationToken);
            return;
        }

        var forge = forges.For(repository.Provider);
        if (forge is null || !forge.Capabilities.Contains(EForgeCapability.GitMirror))
        {
            await RecordAsync(stateId, "refused", "unsupported", null, cancellationToken);
            return;
        }

        ForgeGitAccess? access;
        try
        {
            access = await forge.GetGitAccessAsync(repository, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not obtain git access for {FullName}", repository.FullName);
            await RecordAsync(stateId, "failed", "auth", null, cancellationToken);
            return;
        }

        if (access is null)
        {
            // A private repository with no credential left behind it. Whatever was mirrored while
            // there was one must not outlive the access (PRD §5.2).
            if (state.HasMirror)
                await DeleteAsync(repositoryId, "no-access", cancellationToken: cancellationToken);
            else
                await RecordAsync(stateId, "refused", "no-access", null, cancellationToken);
            return;
        }

        var cloning = !state.HasMirror;
        if (cloning)
        {
            if (access.InstallationKey is { } installation && await InstallationOverCapAsync(installation, stateId, cancellationToken))
            {
                logger.LogInformation("Refused a mirror of {FullName}: installation {Installation} is at its cap", repository.FullName, installation);
                await RecordAsync(stateId, "refused", "installation-cap", null, cancellationToken);
                await BumpMetricsAsync(m => m.Refusals++, cancellationToken);
                return;
            }

            await TrimToBudgetAsync(stateId, beforeClone: true, cancellationToken);
        }

        var tier = access.IsAnonymous ? options.Value.Anonymous : options.Value.Installed;
        var request = new SidecarFetchRequest(
            access.CloneUrl,
            access.Password is null ? null : new SidecarCredential(access.Username ?? string.Empty, access.Password),
            new SidecarQuota(tier.MaxBytes, tier.MaxRefs, tier.TimeoutSeconds),
            // D40: the app's HasMirror can be wrong (a lost volume), so the sidecar is told too: only a
            // view-initiated request may clone, everything else refreshes an existing mirror or nothing.
            allowClone ? SidecarFetchModes.CloneOrFetch : SidecarFetchModes.FetchOnly);

        SidecarFetchResult result;
        try
        {
            result = await sidecar.FetchAsync(repositoryId, request, TimeSpan.FromSeconds(tier.TimeoutSeconds) + FetchMargin, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Recorded, not rethrown: the next read or the hourly net asks again, and a redelivery
            // loop against a sidecar that is down would only pile up.
            logger.LogWarning(ex, "The mirror sidecar failed to fetch {FullName}", repository.FullName);
            await RecordAsync(stateId, "failed", "error", null, cancellationToken);
            return;
        }

        var stillWanted = await UpdateStateAsync(stateId, s =>
        {
            s.Outcome = result.Outcome;
            s.Reason = result.Reason;
            s.LastFetchAtUtc = Now;
            s.Syncing = false;
            s.SyncingSinceUtc = null;
            // A failed or refused clone leaves no directory behind (contract); a failed fetch of an
            // existing mirror leaves the mirror as it was. ⚠️ Except a fetch that pushed the mirror
            // over a quota: the sidecar removes it then. Left marked as a mirror, it kept its size
            // in the budget and the hourly net kept asking for a fetch, which the sidecar runs as a
            // full clone of a mirror it no longer has, only to refuse it again, every hour.
            s.HasMirror = result.Succeeded || (!cloning && !RemovedBySidecar(result));
            if (!s.HasMirror) s.SizeBytes = 0;
            if (result.Succeeded)
            {
                s.SizeBytes = result.SizeBytes;
                s.RefsHash = result.RefsHash;
                s.CommitCount = result.CommitCount;
                s.InstallationId = access.InstallationKey;
            }
            return true;
        }, cancellationToken);

        if (stillWanted is null)
        {
            // Deleted while the fetch ran (a disconnect, an eviction). The sidecar's DELETE waits
            // for an in-flight fetch, but this one may have started after it; a second DELETE is
            // idempotent and leaves no mirror without a state.
            await sidecar.DeleteAsync(repositoryId, cancellationToken);
            return;
        }

        if (result.Outcome == "refused" && !MirrorMissing(result)) await BumpMetricsAsync(m => m.Refusals++, cancellationToken);
        if (cloning && result.Succeeded) await TrimToBudgetAsync(stateId, beforeClone: false, cancellationToken);
    }

    /// <summary>
    /// Whether the sidecar has no mirror after this fetch although one existed: a fetch that left it over the
    /// size or ref quota (the sidecar removes it then), or a <c>fetch-only</c> that found none (D40, a lost
    /// volume). Either way the state becomes mirror-less, so the next view clones it again. A refusal at the
    /// disk floor happens before the mirror is touched, so it stays.
    /// </summary>
    private static bool RemovedBySidecar(SidecarFetchResult result)
        => result.Outcome == "refused" && result.Reason is "too-large" or "too-many-refs" or "missing";

    /// <summary>A <c>fetch-only</c> found no mirror (D40): not a refusal of anything, so not counted as one.</summary>
    internal static bool MirrorMissing(SidecarFetchResult result)
        => result.Outcome == "refused" && result.Reason == "missing";

    public async Task DeleteAsync(string repositoryId, string reason, bool eviction = false, CancellationToken cancellationToken = default)
    {
        if (sidecar.IsConfigured && CommitGraphSidecarClient.TryPath(repositoryId, out _))
            await sidecar.DeleteAsync(repositoryId, cancellationToken);

        using var session = store.OpenAsyncSession();
        session.Delete(RepositoryGraphState.ForRepository(repositoryId));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted the commit-graph mirror of {RepositoryId} ({Reason})", repositoryId, reason);
        if (eviction)
            await BumpMetricsAsync(m => { m.Evictions++; m.LastEvictionAtUtc = Now; }, cancellationToken);
    }

    public async Task<int> EvictIdleAsync(CancellationToken cancellationToken = default)
    {
        var states = await ListStatesAsync(cancellationToken);
        if (states.Count == 0) return 0;

        Dictionary<string, Repository> repositories;
        using (var session = store.OpenAsyncSession())
            repositories = (await session.LoadAsync<Repository>(states.Select(s => s.Repository).Distinct(), cancellationToken))!;

        var cutoff = Now.AddDays(-options.Value.EvictAfterDays);
        var removed = 0;
        foreach (var state in states)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var repository = repositories.GetValueOrDefault(state.Repository);
                if (repository is null || repository.Connection == RepositoryConnection.Disconnected)
                {
                    // Belt and braces for a lost-access path that did not reach the queue.
                    await DeleteAsync(state.Repository, "disconnected", cancellationToken: cancellationToken);
                    removed++;
                }
                else if ((state.LastViewedAtUtc ?? DateTime.MinValue) < cutoff)
                {
                    await DeleteAsync(state.Repository, "idle", eviction: true, cancellationToken);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not evict the mirror of {RepositoryId}; tomorrow's run retries", state.Repository);
            }
        }
        return removed;
    }

    public async Task<int> QueueRefreshesAsync(CancellationToken cancellationToken = default)
    {
        if (!sidecar.IsConfigured) return 0;

        var now = time.GetUtcNow();
        var due = now.UtcDateTime.AddHours(-1);
        var queued = 0;
        foreach (var state in await ListStatesAsync(cancellationToken))
        {
            if (!state.HasMirror || (state.LastFetchAtUtc ?? DateTime.MinValue) > due) continue;
            await messageBus.BroadcastOnceAsync(
                new FetchCommitGraphMessage { RepositoryId = state.Repository, AllowClone = false },
                CommitGraphTriggers.HourlyKey(state.Repository, now), cancellationToken);
            queued++;
        }
        return queued;
    }

    // ── Budget ───────────────────────────────────────────────────────────────────────────────────

    private async Task<bool> InstallationOverCapAsync(string installation, string exceptStateId, CancellationToken cancellationToken)
    {
        var used = (await ListStatesAsync(cancellationToken))
            .Where(s => s.HasMirror && s.Id != exceptStateId && s.InstallationId == installation)
            .Sum(s => s.SizeBytes);
        return used >= options.Value.InstallationCapBytes;
    }

    /// <summary>
    /// Evicts least-recently-viewed mirrors (never <paramref name="protectedStateId"/>) until the
    /// total is under the budget: strictly below it before a clone, at most equal after one.
    /// </summary>
    private async Task TrimToBudgetAsync(string protectedStateId, bool beforeClone, CancellationToken cancellationToken)
    {
        var budget = options.Value.BudgetBytes;
        var mirrors = (await ListStatesAsync(cancellationToken)).Where(s => s.HasMirror).ToList();
        var total = mirrors.Sum(s => s.SizeBytes);

        foreach (var victim in mirrors
                     .Where(s => s.Id != protectedStateId)
                     .OrderBy(s => s.LastViewedAtUtc ?? DateTime.MinValue)
                     .ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            if (beforeClone ? total < budget : total <= budget) break;
            try
            {
                await DeleteAsync(victim.Repository, "budget", eviction: true, cancellationToken);
                total -= victim.SizeBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not evict {RepositoryId} for space", victim.Repository);
            }
        }
    }

    private async Task<List<RepositoryGraphState>> ListStatesAsync(CancellationToken cancellationToken)
    {
        // By id prefix, so no index and no staleness: the eviction that follows must see the clone
        // that just finished.
        const int page = 1024;
        var all = new List<RepositoryGraphState>();
        using var session = store.OpenAsyncSession();
        for (var start = 0; start < MaxStates; start += page)
        {
            var batch = await session.Advanced.LoadStartingWithAsync<RepositoryGraphState>(
                RepositoryGraphState.Prefix, start: start, pageSize: page, token: cancellationToken);
            var list = batch.ToList();
            all.AddRange(list);
            if (list.Count < page) break;
        }
        return all;
    }

    // ── State writes ─────────────────────────────────────────────────────────────────────────────

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private Task RecordAsync(string stateId, string outcome, string? reason, long? size, CancellationToken cancellationToken)
        => UpdateStateAsync(stateId, s =>
        {
            s.Outcome = outcome;
            s.Reason = reason;
            s.LastFetchAtUtc = Now;
            s.Syncing = false;
            s.SyncingSinceUtc = null;
            if (size is { } bytes) s.SizeBytes = bytes;
            return true;
        }, cancellationToken);

    private Task ClearSyncingAsync(string stateId, CancellationToken cancellationToken)
        => UpdateStateAsync(stateId, s =>
        {
            if (!s.Syncing) return false;
            s.Syncing = false;
            s.SyncingSinceUtc = null;
            return true;
        }, cancellationToken);

    /// <summary>
    /// Load, mutate, save under the change vector; retried on a concurrent write. Null when the
    /// state no longer exists, which is never recreated here.
    /// </summary>
    internal async Task<RepositoryGraphState?> UpdateStateAsync(
        string stateId, Func<RepositoryGraphState, bool> mutate, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
            var state = await session.LoadAsync<RepositoryGraphState>(stateId, cancellationToken);
            if (state is null) return null;
            if (!mutate(state)) return state;
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return state;
            }
            catch (ConcurrencyException) when (attempt < 4)
            {
                // Someone else wrote it (a page read stamping LastViewedAtUtc); reload and reapply.
            }
        }
    }

    private async Task BumpMetricsAsync(Action<CommitGraphMetrics> bump, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
            var metrics = await session.LoadAsync<CommitGraphMetrics>(CommitGraphMetrics.DocumentId, cancellationToken);
            if (metrics is null)
            {
                metrics = new CommitGraphMetrics();
                await session.StoreAsync(metrics, changeVector: string.Empty, CommitGraphMetrics.DocumentId, cancellationToken);
            }
            bump(metrics);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ConcurrencyException) when (attempt < 4)
            {
            }
        }
    }
}
