using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Challenges;
using MintPlayer.Spark.Challenges.Deceptive;
using MintPlayer.Spark.Challenges.Risk;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.WaitingRoom.Admissions;
using MintPlayer.Spark.WaitingRoom.Contracts;
using MintPlayer.Spark.WaitingRoom.Live;
using MintPlayer.Spark.WaitingRoom.Services;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.WaitingRoom;

/// <summary>
/// Waiting rooms (<c>docs/commerce_PRD.md</c> §4.12, <c>docs/guide-waiting-room.md</c>): rooms by code, and the visitor's
/// own entry by the current request's identity (X1).
/// </summary>
public interface ISparkWaitingRooms
{
    /// <summary>
    /// Creates or replaces the room <paramref name="key"/> (<c>WaitingRooms/{key}</c>). Its state, with the seed and its
    /// commitment, is created on first save and kept afterwards. Refuses a RandomizedPreQueue or Ballot room without
    /// sign-in unless <see cref="WaitingRoomDefinition.AllowGuestsInFairModes"/> (D10).
    /// </summary>
    /// <exception cref="ArgumentException">An invalid key or definition.</exception>
    Task<WaitingRoomDefinition> SaveRoomAsync(string key, WaitingRoomDefinition room, CancellationToken cancellationToken = default);

    /// <summary>The room and its state, or null.</summary>
    Task<(WaitingRoomDefinition Room, WaitingRoomState? State)?> GetRoomAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>The room as its page shows it, or null.</summary>
    Task<WaitingRoomInfo?> InfoAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Joins (idempotent per identity: a refresh, a tab or a device gets the same entry). Null when the room does not exist.</summary>
    Task<WaitingRoomJoinResult?> JoinAsync(string key, WaitingRoomJoinBody body, CancellationToken cancellationToken = default);

    /// <summary>The caller's entry, or null.</summary>
    Task<WaitingRoomEntryView?> GetEntryAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// A sign of life: answers a presence check (admits the entry while its check runs), brings a missed entry back at its
    /// rank, and records the tab's visibility. Null when the caller has no entry.
    /// </summary>
    Task<WaitingRoomEntryView?> PresenceAsync(string key, bool visible, CancellationToken cancellationToken = default);

    /// <summary>Leaves the queue; a later join starts at the back. False when there was no entry.</summary>
    Task<bool> LeaveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>The proof of the shuffle or draw (the seed only once revealed), or null.</summary>
    Task<WaitingRoomDrawProof?> DrawProofAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Marks the room sold out for good (or reopens it), when the host knows better than a capacity source.</summary>
    Task MarkSoldOutAsync(string key, bool soldOut, CancellationToken cancellationToken = default);
}

internal sealed class SparkWaitingRooms(
    IDocumentStore store,
    ISparkClaims claims,
    WaitingRoomTokens tokens,
    WaitingRoomPublisher publisher,
    WaitingRoomGateCache gates,
    IHttpContextAccessor accessor,
    IOptions<SparkWaitingRoomOptions> options,
    IServiceProvider services,
    TimeProvider time,
    ILogger<SparkWaitingRooms> logger) : ISparkWaitingRooms
{
    public async Task<WaitingRoomDefinition> SaveRoomAsync(string key, WaitingRoomDefinition room, CancellationToken cancellationToken = default)
    {
        if (!WaitingRoomStore.IsValidKey(key))
            throw new ArgumentException($"A waiting room key is lower-case letters, digits, '.', '_' and '-', at most 100 characters: '{key}'.", nameof(key));
        if (Validate(room) is { } problem)
            throw new ArgumentException($"Waiting room '{key}': {problem}", nameof(room));

        room.Id = WaitingRoomStore.RoomId(key);
        using var session = WaitingRoomStore.ClusterSession(store);
        var stored = await session.LoadAsync<WaitingRoomDefinition>(room.Id, cancellationToken);
        if (stored is null)
            await session.StoreAsync(room, room.Id, cancellationToken);
        else
            Copy(room, stored);

        var state = await session.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
        if (state is null)
        {
            var seed = WaitingRoomShuffle.NewSeed();
            state = new WaitingRoomState
            {
                Id = WaitingRoomStore.StateId(key),
                RoomId = room.Id,
                Seed = seed,
                SeedCommitment = WaitingRoomShuffle.Commit(seed),
                MinuteStartUtc = time.GetUtcNow().UtcDateTime,
            };
            await session.StoreAsync(state, state.Id, cancellationToken);
        }
        await session.SaveChangesAsync(cancellationToken);
        gates.Invalidate();
        publisher.PublishRoom(WaitingRoomStore.Frame(room, state, time.GetUtcNow()));
        return room;
    }

    /// <summary>The first problem of a definition, or null (also run on configured rooms at startup).</summary>
    internal static string? Validate(WaitingRoomDefinition room)
    {
        if (string.IsNullOrWhiteSpace(room.Gate))
            return "Gate is required (the sale phase or route its admission tokens are bound to).";
        if (room.OpensAt is { } opens && opens > room.StartsAt)
            return "OpensAt is after StartsAt.";
        if (room.EndsAt is { } ends && ends <= room.StartsAt)
            return "EndsAt is not after StartsAt.";
        if (room.Mode is WaitingRoomMode.RandomizedPreQueue or WaitingRoomMode.Ballot
            && !(room.RequireSignIn || room.RequireVerifiedPhone || room.MinimumAccountAge is not null) && !room.AllowGuestsInFairModes)
            return $"{room.Mode} needs RequireSignIn: without sign-in one person is many identities and the shuffle rewards whoever opens the most browsers. Set AllowGuestsInFairModes to override (D10).";
        if (room.Admission.MaxActive < 1 || room.Admission.PerMinute < 1 || room.Admission.UnitsPerAdmission < 1)
            return "Admission.MaxActive, PerMinute and UnitsPerAdmission must be at least 1.";
        if (room.Admission.TokenLifetime < TimeSpan.FromMinutes(1))
            return "Admission.TokenLifetime must be at least a minute (at least the hold plus the payment window).";
        if (room.Mode == WaitingRoomMode.Ballot && (room.Ballot.GroupSize < 1 || room.Ballot.WindowLength < TimeSpan.FromMinutes(1)))
            return "Ballot.GroupSize must be at least 1 and Ballot.WindowLength at least a minute.";
        return null;
    }

    private static void Copy(WaitingRoomDefinition from, WaitingRoomDefinition to)
    {
        to.Title = from.Title;
        to.Gate = from.Gate;
        to.Mode = from.Mode;
        to.OpensAt = from.OpensAt;
        to.StartsAt = from.StartsAt;
        to.EndsAt = from.EndsAt;
        to.RequireSignIn = from.RequireSignIn;
        to.AllowGuestsInFairModes = from.AllowGuestsInFairModes;
        to.RequireVerifiedPhone = from.RequireVerifiedPhone;
        to.MinimumAccountAge = from.MinimumAccountAge;
        to.ChallengeAtJoin = from.ChallengeAtJoin;
        to.Admission = from.Admission;
        to.Ballot = from.Ballot;
        to.CapacityKey = from.CapacityKey;
        to.AdmittedUrl = from.AdmittedUrl;
    }

    public async Task<(WaitingRoomDefinition Room, WaitingRoomState? State)?> GetRoomAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!WaitingRoomStore.IsValidKey(key))
            return null;
        using var session = store.OpenAsyncSession();
        var room = await session.LoadAsync<WaitingRoomDefinition>(WaitingRoomStore.RoomId(key), cancellationToken);
        if (room is null)
            return null;
        return (room, await session.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken));
    }

    public async Task<WaitingRoomInfo?> InfoAsync(string key, CancellationToken cancellationToken = default)
    {
        if (await GetRoomAsync(key, cancellationToken) is not ({ } room, var state))
            return null;
        var now = time.GetUtcNow();
        var challenges = services.GetService<ISparkHumanChallenges>();
        var joinChallenge = challenges?.IsConfigured == true
            && (room.ChallengeAtJoin || await challenges.IsEnabledAtAsync(SparkChallengePoints.QueueJoin, cancellationToken));
        return new WaitingRoomInfo(
            key, room.Title, room.Mode.ToString(), WaitingRoomStore.StatusAt(room, state, now),
            room.OpensAt, room.StartsAt, room.EndsAt,
            room.RequireSignIn || room.RequireVerifiedPhone || room.MinimumAccountAge is not null,
            state?.ServedUpTo ?? 0, Math.Max(0, (state?.RankedCount ?? 0) - (state?.ServedUpTo ?? 0)),
            state?.SeedCommitment ?? "", state?.SeedRevealedAtUtc is null ? null : state.Seed,
            joinChallenge);
    }

    public async Task<WaitingRoomJoinResult?> JoinAsync(string key, WaitingRoomJoinBody body, CancellationToken cancellationToken = default)
    {
        if (await GetRoomAsync(key, cancellationToken) is not ({ } room, var state))
            return null;
        var http = accessor.HttpContext ?? throw new InvalidOperationException("Joining a waiting room is a request operation.");
        var visitor = http.Features.Get<ISparkVisitor>() ?? throw new InvalidOperationException("The visitor feature (X1) is not registered: call UseSpark().");
        var now = time.GetUtcNow();
        var status = WaitingRoomStore.StatusAt(room, state, now);

        // The caller's existing entry answers first: a refresh never re-asks a challenge or re-judges eligibility.
        var identity = visitor.UserId is { } user ? "user:" + user : visitor.VisitorId is { } v ? "visitor:" + v : null;
        if (identity is not null && await FindEntryAsync(key, identity, cancellationToken) is { } existing)
            return new("joined", WaitingRoomStore.ToView(existing, room, state));

        switch (status)
        {
            case WaitingRoomStatus.Closed: return new("closed");
            case WaitingRoomStatus.SoldOut: return new("sold-out");
            case WaitingRoomStatus.Scheduled: return new("not-open");
        }
        if (room.Mode == WaitingRoomMode.Ballot && now >= room.StartsAt)
            return new("registration-closed");

        var needsAccount = room.RequireSignIn || room.RequireVerifiedPhone || room.MinimumAccountAge is not null;
        if (needsAccount && visitor.UserId is null)
            return new("sign-in-required");
        if (visitor.UserId is not null && (room.RequireVerifiedPhone || room.MinimumAccountAge is not null)
            && await EligibilityAsync(room, visitor.UserId, now, cancellationToken) is { } refusal)
            return new(refusal);

        // Risk and the deceptive measures, when the host registered the challenges package.
        var shadow = false;
        var challengeAsked = false;
        if (services.GetService<ISparkRiskPipeline>() is { } pipeline)
        {
            var facts = new Dictionary<string, string?> { ["honeypot"] = body.Website, ["webdriver"] = body.Webdriver is true ? "true" : null };
            var context = pipeline.ContextFor(http, SparkChallengePoints.QueueJoin, room.Id, facts);
            var decision = await pipeline.EvaluateAsync(context, cancellationToken);
            var deceptive = services.GetService<ISparkDeceptiveMeasures>();
            if (deceptive is not null && await deceptive.ApplyAsync(SparkDeceptiveMeasureNames.Tarpit, context, decision, cancellationToken))
                await Task.Delay(deceptive.TarpitDelay, time, cancellationToken);
            switch (decision.Action)
            {
                case SparkRiskAction.Block:
                    if (deceptive is not null && await deceptive.ApplyAsync(SparkDeceptiveMeasureNames.FakeSoldOut, context, decision, cancellationToken))
                        return new("sold-out");
                    return new("refused", DecisionId: decision.DecisionId);
                case SparkRiskAction.ShadowQueue:
                    shadow = true;
                    break;
                case SparkRiskAction.Slow:
                    return new("slow-down", RetryAfterSeconds: (int)Math.Ceiling((decision.RetryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds));
                case SparkRiskAction.Challenge:
                    challengeAsked = true;
                    break;
            }
            if (deceptive is not null && await deceptive.ApplyAsync(SparkDeceptiveMeasureNames.Honeypot, context, decision, cancellationToken))
                shadow = true;
        }

        var challengePassed = false;
        if (services.GetService<ISparkHumanChallenges>() is { IsConfigured: true } challenges
            && (challengeAsked || room.ChallengeAtJoin || await challenges.IsEnabledAtAsync(SparkChallengePoints.QueueJoin, cancellationToken)))
        {
            if (body.Challenge is null || !(await challenges.VerifyAsync(SparkChallengePoints.QueueJoin, body.Challenge, key, cancellationToken)).Passed)
                return new("challenge", Challenge: await challenges.OfferAsync(SparkChallengePoints.QueueJoin, key, cancellationToken));
            challengePassed = true;
        }

        identity ??= "visitor:" + await visitor.EnsureAsync();
        var entry = new WaitingRoomEntry
        {
            Id = WaitingRoomStore.EntryPrefix + Guid.NewGuid().ToString("N"),
            RoomId = room.Id!,
            IdentityKey = identity,
            UserId = visitor.UserId,
            VisitorId = visitor.UserId is null ? identity["visitor:".Length..] : visitor.VisitorId,
            PreQueue = now < room.StartsAt,
            Shadow = shadow,
            ChallengePassed = challengePassed,
            LastSeenUtc = now.UtcDateTime,
            ExpiresAtUtc = ((room.EndsAt ?? room.StartsAt) + options.Value.EntryRetention).UtcDateTime,
        };

        using (var unit = claims.BeginUnitOfWork())
        {
            var reserved = await unit.ReserveAsync(WaitingRoomStore.Identities, WaitingRoomStore.IdentityKey(key, identity), identity,
                _ => new SparkClaimHold(identity, null, entry.Id), cancellationToken: cancellationToken);
            if (reserved.Status == SparkClaimStatus.Reserved)
            {
                await unit.Session.StoreAsync(entry, entry.Id, cancellationToken);
                if (await unit.CommitAsync(cancellationToken))
                    return new("joined", WaitingRoomStore.ToView(entry, room, state));
            }
        }
        // Another tab or device of the same identity joined first: theirs is ours.
        return await FindEntryAsync(key, identity, cancellationToken) is { } winner
            ? new("joined", WaitingRoomStore.ToView(winner, room, state))
            : throw new InvalidOperationException("Joining the waiting room conflicted and no entry was found; try again.");
    }

    private async Task<string?> EligibilityAsync(WaitingRoomDefinition room, string userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var user = await session.LoadAsync<SparkUser>(userId, cancellationToken);
        if (room.RequireVerifiedPhone && user?.PhoneNumberConfirmed != true)
            return "phone-verification-required";
        if (room.MinimumAccountAge is { } age && (user?.CreatedAtUtc is not { } created || now.UtcDateTime - created < age))
            return "account-too-new";
        return null;
    }

    public async Task<WaitingRoomEntryView?> GetEntryAsync(string key, CancellationToken cancellationToken = default)
    {
        if (await GetRoomAsync(key, cancellationToken) is not ({ } room, var state) || CurrentIdentity() is not { } identity)
            return null;
        return await FindEntryAsync(key, identity, cancellationToken) is { } entry ? WaitingRoomStore.ToView(entry, room, state) : null;
    }

    public async Task<WaitingRoomEntryView?> PresenceAsync(string key, bool visible, CancellationToken cancellationToken = default)
    {
        if (await GetRoomAsync(key, cancellationToken) is not ({ } room, var state) || CurrentIdentity() is not { } identity)
            return null;
        if (await FindClaimAsync(key, identity, cancellationToken) is not { } entryId)
            return null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = time.GetUtcNow().UtcDateTime;
            using var session = WaitingRoomStore.ClusterSession(store);
            var entry = await session.LoadAsync<WaitingRoomEntry>(entryId, cancellationToken);
            if (entry is null)
                return null;
            var admitted = false;
            var missedBack = false;
            var changed = entry.Hidden == visible;
            entry.Hidden = !visible;
            if (entry.Status == WaitingRoomEntryStatus.Called && !entry.Shadow && entry.PresenceDeadlineUtc >= now)
            {
                WaitingRoomStore.Admit(entry, room, tokens, now, room.Admission.TokenLifetime, await WaitingRoomStore.ShouldPoisonAsync(services, entry, cancellationToken));
                admitted = changed = true;
            }
            else if (entry.Status == WaitingRoomEntryStatus.Missed)
            {
                // WCAG 2.2.1: a missed check never costs the place; the entry is next in line again at its own rank.
                entry.Status = WaitingRoomEntryStatus.Waiting;
                entry.PresenceDeadlineUtc = null;
                missedBack = changed = true;
            }
            if (entry.LastSeenUtc is not { } seen || now - seen > TimeSpan.FromSeconds(30))
            {
                entry.LastSeenUtc = now;
                changed = true;
            }
            if (!changed)
                return WaitingRoomStore.ToView(entry, room, state);

            WaitingRoomStore.CaptureArrival(session, entry);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyException)
            {
                continue; // the worker changed it meanwhile (called it, or ended its check): judge again
            }
            if (admitted)
            {
                publisher.PublishPrivate(key, entry.IdentityKey, new WaitingRoomPrivateFrame("admitted", entry.Id!, null, entry.Token,
                    WaitingRoomStore.Utc(entry.ActiveUntilUtc), WaitingRoomStore.AdmittedUrl(room, entry.Token)));
                await NotifyAsync("admitted", room, entry, cancellationToken);
            }
            else if (missedBack)
                logger.LogDebug("Entry {Entry} came back after a missed presence check; it keeps rank {Rank}.", entry.Id, entry.Rank);
            return WaitingRoomStore.ToView(entry, room, state);
        }
        return await GetEntryAsync(key, cancellationToken);
    }

    public async Task<bool> LeaveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!WaitingRoomStore.IsValidKey(key) || CurrentIdentity() is not { } identity)
            return false;
        using var unit = claims.BeginUnitOfWork();
        var claimKey = WaitingRoomStore.IdentityKey(key, identity);
        var released = await unit.ReleaseAsync(WaitingRoomStore.Identities, claimKey, identity, cancellationToken);
        if (released.Status != SparkClaimStatus.Released)
            return false;
        if (released.Claim?.Value.Data is { } entryId && await unit.Session.LoadAsync<WaitingRoomEntry>(entryId, cancellationToken) is { } entry)
        {
            WaitingRoomStore.CaptureArrival(unit.Session, entry);
            entry.Status = WaitingRoomEntryStatus.Left;
        }
        return await unit.CommitAsync(cancellationToken);
    }

    public async Task<WaitingRoomDrawProof?> DrawProofAsync(string key, CancellationToken cancellationToken = default)
    {
        if (await GetRoomAsync(key, cancellationToken) is not (_, { } state))
            return null;
        if (state.SeedRevealedAtUtc is null)
            return new WaitingRoomDrawProof(WaitingRoomShuffle.Algorithm, state.SeedCommitment, null, null, 0, null);
        IReadOnlyList<string>? ids = null;
        if (state.ShuffledCount <= 50_000)
        {
            var roomId = WaitingRoomStore.RoomId(key);
            var count = state.ShuffledCount;
            var list = new List<string>();
            using var session = store.OpenAsyncSession();
            var query = session.Query<WaitingRoomEntry>().Where(e => e.RoomId == roomId && e.Rank != null && e.Rank <= count);
            await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
                list.Add(stream.Current.Id);
            list.Sort(StringComparer.Ordinal);
            ids = list;
        }
        return new WaitingRoomDrawProof(WaitingRoomShuffle.Algorithm, state.SeedCommitment, state.Seed, state.DrawInputHash, state.ShuffledCount, ids);
    }

    public async Task MarkSoldOutAsync(string key, bool soldOut, CancellationToken cancellationToken = default)
    {
        using var session = WaitingRoomStore.ClusterSession(store);
        var state = await session.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken)
            ?? throw new ArgumentException($"No waiting room '{key}'.", nameof(key));
        state.Status = soldOut ? WaitingRoomStatus.SoldOut : WaitingRoomStatus.Open;
        await session.SaveChangesAsync(cancellationToken);
        if (await session.LoadAsync<WaitingRoomDefinition>(WaitingRoomStore.RoomId(key), cancellationToken) is { } room)
            publisher.PublishRoom(WaitingRoomStore.Frame(room, state, time.GetUtcNow()));
    }

    private string? CurrentIdentity()
    {
        var visitor = accessor.HttpContext?.Features.Get<ISparkVisitor>();
        return visitor?.UserId is { } user ? "user:" + user : visitor?.VisitorId is { } v ? "visitor:" + v : null;
    }

    private async Task<string?> FindClaimAsync(string key, string identity, CancellationToken cancellationToken)
        => (await claims.GetAsync(WaitingRoomStore.Identities, WaitingRoomStore.IdentityKey(key, identity), cancellationToken))?.Value.Data;

    private async Task<WaitingRoomEntry?> FindEntryAsync(string key, string identity, CancellationToken cancellationToken)
    {
        if (await FindClaimAsync(key, identity, cancellationToken) is not { } entryId)
            return null;
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<WaitingRoomEntry>(entryId, cancellationToken);
    }

    private async Task NotifyAsync(string kind, WaitingRoomDefinition room, WaitingRoomEntry entry, CancellationToken cancellationToken)
    {
        foreach (var notifier in services.GetServices<IWaitingRoomNotifier>())
        {
            try
            {
                await notifier.OnEntryChangedAsync(kind, room, entry, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Notifier} failed for entry {Entry} ({Kind}).", notifier.GetType().Name, entry.Id, kind);
            }
        }
    }
}
