using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Moderation.Documents;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>What Moderation knows about the current user, read once per request.</summary>
internal sealed class ModerationUserSnapshot
{
    public required string UserId { get; init; }
    public ModerationAccount? Account { get; init; }
    public ReputationSummary? Summary { get; init; }
    public ModerationProfile? Profile { get; init; }
    public ModerationSuspension? Suspension { get; init; }
    public required DateTime NowUtc { get; init; }

    public bool IsSuspended => Suspension is { } s && (s.UntilUtc is null || s.UntilUtc > NowUtc);
    public int AgeDays => Account?.AgeDays(NowUtc) ?? 0;
    public int ActiveDays => Profile?.ActiveDays ?? 0;
    public int PrivilegeReputation => Summary?.PrivilegeReputation ?? 0;
    public int Reputation => Summary?.Total ?? 0;
}

/// <summary>
/// The per-request read of the current user's moderation state: summary, profile and suspension are
/// read by id in <b>one</b> request (lazy loads), so a suspension is effective on the very next
/// request — never an index, never a TTL cache (§3.12). Also records activity (the minActiveDays
/// gate) and the network observation, at most once per user and day.
/// </summary>
internal sealed partial class ModerationUserState
{
    // Process-wide memo of (database, user, day, network) already recorded, so an active user costs
    // one write a day, not one per request.
    private static readonly ConcurrentDictionary<string, byte> Recorded = new();

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IModerationAccounts accounts;
    [Inject] private readonly ModerationIpHasher ipHasher;
    [Inject] private readonly IOptions<SparkModerationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<ModerationUserState> logger;
    [Inject] private readonly IHttpContextAccessor? httpContextAccessor;

    private Task<ModerationUserSnapshot?>? current;

    /// <summary>The current user's snapshot, or null when the caller has no user id.</summary>
    public Task<ModerationUserSnapshot?> GetCurrentAsync() => current ??= ReadCurrentAsync();

    /// <summary>Whether the current user is suspended right now.</summary>
    public async Task<bool> IsCurrentUserSuspendedAsync() => (await GetCurrentAsync())?.IsSuspended == true;

    private async Task<ModerationUserSnapshot?> ReadCurrentAsync()
    {
        if (!currentUser.IsAuthenticated || currentUser.Id is not { Length: > 0 } userId)
            return null;
        var snapshot = await ReadAsync(userId);
        // Today counts: the profile after recording is the one the gates judge.
        var profile = await RecordActivityAsync(snapshot);
        return ReferenceEquals(profile, snapshot.Profile) ? snapshot : new ModerationUserSnapshot
        {
            UserId = snapshot.UserId,
            Account = snapshot.Account,
            Summary = snapshot.Summary,
            Profile = profile,
            Suspension = snapshot.Suspension,
            NowUtc = snapshot.NowUtc,
        };
    }

    /// <summary>Reads any user's snapshot (the vote path reads the voter's).</summary>
    public async Task<ModerationUserSnapshot> ReadAsync(string userId)
    {
        using var session = documentStore.OpenAsyncSession();
        var summary = session.Advanced.Lazily.LoadAsync<ReputationSummary>(ModerationIds.Summary(userId));
        var profile = session.Advanced.Lazily.LoadAsync<ModerationProfile>(ModerationIds.Profile(userId));
        var suspension = session.Advanced.Lazily.LoadAsync<ModerationSuspension>(ModerationIds.Suspension(userId));
        await session.Advanced.Eagerly.ExecuteAllPendingLazyOperationsAsync();
        var account = (await accounts.GetAsync([userId])).GetValueOrDefault(userId);

        return new ModerationUserSnapshot
        {
            UserId = userId,
            Account = account,
            Summary = await summary.Value,
            Profile = await profile.Value,
            Suspension = await suspension.Value,
            NowUtc = timeProvider.GetUtcNow().UtcDateTime,
        };
    }

    private async Task<ModerationProfile?> RecordActivityAsync(ModerationUserSnapshot snapshot)
    {
        var day = ModerationIds.Day(snapshot.NowUtc);
        var result = snapshot.Profile;
        try
        {
            var memo = $"{documentStore.Database}|{snapshot.UserId}|{day}";
            if (snapshot.Profile?.LastActiveDay != day && Recorded.TryAdd(memo, 0))
            {
                using var session = documentStore.OpenAsyncSession();
                session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
                var profile = await session.LoadAsync<ModerationProfile>(ModerationIds.Profile(snapshot.UserId))
                              ?? new ModerationProfile { UserId = snapshot.UserId };
                if (profile.LastActiveDay != day)
                {
                    profile.ActiveDays++;
                    profile.LastActiveDay = day;
                    await session.StoreAsync(profile, ModerationIds.Profile(snapshot.UserId));
                    await session.SaveChangesAsync();
                }
                result = profile;
            }
            await RecordNetworkAsync(snapshot, day);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bookkeeping must never fail the request that triggered it.
            logger.LogWarning(ex, "Moderation could not record activity for {UserId}.", snapshot.UserId);
        }
        return result;
    }

    /// <summary>Fraud measure 9: one hashed network observation per user, day and network, expiring after the retention.</summary>
    private async Task RecordNetworkAsync(ModerationUserSnapshot snapshot, string day)
    {
        {
            var address = httpContextAccessor?.HttpContext?.Connection.RemoteIpAddress;
            if (address is null)
                return;
            var (keyId, hash) = await ipHasher.HashAsync(address);
            var observationId = ModerationIds.IpObservation(snapshot.UserId, day, hash);
            if (!Recorded.TryAdd($"{documentStore.Database}|{observationId}", 0))
                return;
            using var ipSession = documentStore.OpenAsyncSession();
            if (await ipSession.Advanced.ExistsAsync(observationId))
                return;
            var observation = new ModerationIpObservation { UserId = snapshot.UserId, KeyId = keyId, Hash = hash, Day = day };
            await ipSession.StoreAsync(observation, observationId);
            ipSession.Advanced.GetMetadataFor(observation)["@expires"] =
                snapshot.NowUtc.AddDays(options.Value.Fraud.IpObservationRetentionDays).ToString("O");
            await ipSession.SaveChangesAsync();
        }
    }
}

/// <summary>
/// Privileges earned by reputation, surfaced as <c>security.json</c> groups by id (D12), composed
/// with the application's own membership (<c>AddGroupMembershipProvider</c>, request-cached by core).
/// </summary>
/// <remarks>
/// Fraud measure 1: every privilege has a reputation, account-age and active-days gate. A suspended
/// account earns none (§3.12). Well-known and undeclared ids are dropped by core, whatever this returns.
/// </remarks>
internal sealed partial class ModerationPrivilegeProvider : IGroupMembershipProvider, IGroupIdMembershipProvider
{
    [Inject] private readonly ModerationUserState userState;
    [Inject] private readonly IOptions<SparkModerationOptions> options;

    public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<string>>([]);

    public async Task<IEnumerable<Guid>> GetCurrentUserGroupIdsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await userState.GetCurrentAsync();
        return snapshot is null ? [] : Earned(snapshot, options.Value).Select(p => p.Value.GroupId).Distinct().ToList();
    }

    /// <summary>The privileges <paramref name="snapshot"/> has earned.</summary>
    public static IEnumerable<KeyValuePair<string, ModerationPrivilegeOptions>> Earned(ModerationUserSnapshot snapshot, SparkModerationOptions options)
    {
        if (snapshot.IsSuspended)
            return [];
        return options.Privileges.Where(p =>
            snapshot.PrivilegeReputation >= p.Value.Rep
            && snapshot.AgeDays >= p.Value.MinAccountAgeDays
            && snapshot.ActiveDays >= p.Value.MinActiveDays);
    }
}
