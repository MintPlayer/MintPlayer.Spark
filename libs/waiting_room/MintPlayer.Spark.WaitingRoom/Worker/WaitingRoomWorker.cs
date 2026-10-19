using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.WaitingRoom.Admissions;
using MintPlayer.Spark.WaitingRoom.Contracts;
using MintPlayer.Spark.WaitingRoom.Live;
using MintPlayer.Spark.WaitingRoom.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.WaitingRoom.Worker;

/// <summary>
/// The admission worker (commerce PRD §4.12), every second by default. Per room, under a lease
/// (<c>waitingroom/leases/{key}</c>, X12) so one instance works a room at a time and another takes over when the holder
/// dies (failover), it:
/// <list type="number">
/// <item>shuffles the pre-queue at the start (RandomizedPreQueue) or draws the ballot, with the committed seed;</item>
/// <item>ranks latecomers about once per second, by server arrival time (dense ranks from a single ranker: no hot sequence key);</item>
/// <item>ends presence checks that were not answered (the entry keeps its rank) and admissions whose window ended;</item>
/// <item>reads the capacity (sold out, may return);</item>
/// <item>calls the next ranked entries while active visitors are below the maximum, the per-minute rate has room and the
/// projected capacity exceeds what is already admitted; a ballot invites its window's group instead;</item>
/// <item>publishes the room's frame.</item>
/// </list>
/// Every change to the room state goes through a cluster-wide session that loaded it, so two workers that overlap anyway
/// (a lease lost mid-tick) conflict instead of admitting twice: one commit wins, the other changes nothing.
/// </summary>
internal sealed class WaitingRoomWorker(
    IDocumentStore store,
    ISparkClaims claims,
    WaitingRoomTokens tokens,
    WaitingRoomPublisher publisher,
    IOptions<SparkWaitingRoomOptions> options,
    IServiceProvider services,
    TimeProvider time,
    ILogger<WaitingRoomWorker> logger) : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:WaitingRoom:WorkerSchedule</c> overrides it.</summary>
    public static string CronSchedule => "* * * * * *";

    /// <summary>This process's lease owner name.</summary>
    internal static string InstanceId { get; } = Environment.MachineName + "/" + Guid.NewGuid().ToString("N");

    /// <summary>Overridable in tests to simulate several instances.</summary>
    internal string LeaseOwner { get; set; } = InstanceId;

    private SparkWaitingRoomOptions Options => options.Value;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<WaitingRoomDefinition> rooms;
        using (var session = store.OpenAsyncSession())
            rooms = await session.Query<WaitingRoomDefinition>().Take(1024).ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var room in rooms.Where(r => r.Id is not null && (r.EndsAt is not { } ends || ends > now.AddMinutes(-5))))
        {
            try
            {
                await TickAsync(room, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Waiting room {Room}: the tick failed; the next one retries.", room.Id);
            }
        }
    }

    /// <summary>One room's tick. False when another instance holds its lease.</summary>
    internal async Task<bool> TickAsync(WaitingRoomDefinition room, CancellationToken cancellationToken)
    {
        var key = WaitingRoomStore.KeyOf(room.Id!);
        if (!await AcquireLeaseAsync(key, cancellationToken))
            return false;

        var state = await LoadStateAsync(key, cancellationToken);
        if (state is null)
            return true;
        var now = time.GetUtcNow();

        if (now >= room.StartsAt)
            await CheckCapacityAsync(room, key, state, cancellationToken);
        state = await LoadStateAsync(key, cancellationToken) ?? state;
        var status = WaitingRoomStore.StatusAt(room, state, now);

        if (status == WaitingRoomStatus.Closed || status == WaitingRoomStatus.SoldOut)
            await CloseAsync(room, key, status, cancellationToken);
        else if (room.Mode == WaitingRoomMode.Ballot)
            await BallotAsync(room, key, state, now, cancellationToken);
        else
            await QueueAsync(room, key, state, status, now, cancellationToken);

        if (await LoadStateAsync(key, cancellationToken) is { } latest)
            publisher.PublishRoom(WaitingRoomStore.Frame(room, latest, time.GetUtcNow()));
        return true;
    }

    // ------------------------------------------------------------------ lease

    internal async Task<bool> AcquireLeaseAsync(string key, CancellationToken cancellationToken)
    {
        var leaseKey = WaitingRoomStore.Leases.Key(key);
        var until = (time.GetUtcNow() + Options.LeaseDuration).UtcDateTime;
        var reserved = await claims.ReserveHoldAsync(WaitingRoomStore.Leases, leaseKey, LeaseOwner, until, cancellationToken: cancellationToken);
        if (reserved.Status == SparkClaimStatus.Reserved)
            return true;
        if (reserved.Status != SparkClaimStatus.AlreadyYours)
            return false;
        var renewed = await claims.UpdateAsync(WaitingRoomStore.Leases, leaseKey, LeaseOwner, hold => hold with { ExpiresAtUtc = until }, cancellationToken);
        return renewed.Holds;
    }

    // ------------------------------------------------------------------ queue modes

    private async Task QueueAsync(WaitingRoomDefinition room, string key, WaitingRoomState state, string status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settled = now.UtcDateTime - Options.SettleWindow;
        if (room.Mode == WaitingRoomMode.RandomizedPreQueue && !state.Shuffled && settled >= room.StartsAt.UtcDateTime)
            await ShuffleAsync(room, key, cancellationToken);

        state = await LoadStateAsync(key, cancellationToken) ?? state;
        var ranking = room.Mode == WaitingRoomMode.Fifo ? status != WaitingRoomStatus.Scheduled : state.Shuffled;
        if (ranking)
            await RankLatecomersAsync(room, key, prequeueToo: room.Mode == WaitingRoomMode.Fifo, settled, cancellationToken);

        if (now < room.StartsAt)
            return;
        await ExpireAsync(room, key, now.UtcDateTime, cancellationToken);
        if (status == WaitingRoomStatus.Open)
            await CallAsync(room, key, now.UtcDateTime, cancellationToken);
    }

    /// <summary>
    /// The start-of-sale shuffle (or the ballot draw): every entry that joined before the start (<c>PreQueue</c>, whatever
    /// its status since, so a resumed shuffle draws over the same set) gets ranks 1..n in the seeded order; then the seed is
    /// revealed. Deterministic, so a crash part-way, or a second instance, writes the same ranks.
    /// </summary>
    internal async Task ShuffleAsync(WaitingRoomDefinition room, string key, CancellationToken cancellationToken)
    {
        var roomId = room.Id!;
        var ids = new List<string>();
        using (var session = store.OpenAsyncSession())
        {
            // Make sure every entry committed before the settle point is visible to the stream below.
            await session.Query<WaitingRoomEntry>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(e => e.RoomId == roomId && e.PreQueue)
                .CountAsync(cancellationToken);
            var query = session.Query<WaitingRoomEntry>().Where(e => e.RoomId == roomId && e.PreQueue);
            await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
                ids.Add(stream.Current.Id);
        }

        var state = await LoadStateAsync(key, cancellationToken);
        if (state is null || state.Shuffled)
            return;
        var order = WaitingRoomShuffle.Order(ids, state.Seed, out var inputHash);

        for (var start = 0; start < order.Count; start += Options.BatchSize)
        {
            await AcquireLeaseAsync(key, cancellationToken);
            using var session = WaitingRoomStore.ClusterSession(store);
            var chunk = order.Skip(start).Take(Options.BatchSize).ToArray();
            var entries = await session.LoadAsync<WaitingRoomEntry>(chunk, cancellationToken);
            for (var i = 0; i < chunk.Length; i++)
            {
                if (entries.TryGetValue(chunk[i], out var entry) && entry is not null && entry.Rank is null)
                {
                    WaitingRoomStore.CaptureArrival(session, entry);
                    entry.Rank = start + i + 1;
                }
            }
            await session.SaveChangesAsync(cancellationToken);
        }

        using (var session = WaitingRoomStore.ClusterSession(store))
        {
            var current = await session.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
            if (current is null || current.Shuffled)
                return;
            current.Shuffled = true;
            current.ShuffledCount = order.Count;
            current.RankedCount = Math.Max(current.RankedCount, order.Count);
            current.DrawInputHash = inputHash;
            current.SeedRevealedAtUtc = time.GetUtcNow().UtcDateTime;
            await SaveAsync(session, cancellationToken);
        }
        logger.LogInformation("Waiting room {Room}: {Count} entries shuffled with the committed seed ({Commitment}).", roomId, order.Count, state.SeedCommitment);
    }

    /// <summary>
    /// Ranks entries without a rank in arrival order (server time: <c>@last-modified</c> at creation), leaving the last
    /// <see cref="SparkWaitingRoomOptions.SettleWindow"/> alone so an entry committed a moment earlier but not yet indexed is
    /// not overtaken. The state and the entries commit together, so ranks stay dense.
    /// </summary>
    internal async Task RankLatecomersAsync(WaitingRoomDefinition room, string key, bool prequeueToo, DateTime settledUtc, CancellationToken cancellationToken)
    {
        var roomId = room.Id!;
        List<(string Id, DateTime Arrival)> candidates;
        using (var session = store.OpenAsyncSession())
        {
            var query = session.Query<WaitingRoomEntry>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(e => e.RoomId == roomId && e.Rank == null && e.Status != WaitingRoomEntryStatus.Left);
            if (!prequeueToo)
                query = query.Where(e => !e.PreQueue);
            var found = await query.Take(Options.BatchSize * 4).ToListAsync(cancellationToken);
            candidates = found
                .Select(e => (e.Id!, e.ArrivedAtUtc ?? WaitingRoomStore.LastModified(session, e)))
                .Where(c => c.Item2 <= settledUtc)
                .OrderBy(c => c.Item2).ThenBy(c => c.Item1, StringComparer.Ordinal)
                .Take(Options.BatchSize)
                .ToList();
        }
        if (candidates.Count == 0)
            return;

        using var cluster = WaitingRoomStore.ClusterSession(store);
        var state = await cluster.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
        if (state is null)
            return;
        var entries = await cluster.LoadAsync<WaitingRoomEntry>(candidates.Select(c => c.Id), cancellationToken);
        foreach (var (id, _) in candidates)
        {
            if (!entries.TryGetValue(id, out var entry) || entry is null || entry.Rank is not null)
                continue;
            WaitingRoomStore.CaptureArrival(cluster, entry);
            entry.Rank = ++state.RankedCount;
        }
        await SaveAsync(cluster, cancellationToken);
    }

    /// <summary>Ends unanswered presence checks (the entry keeps its rank: WCAG 2.2.1) and admissions whose window ended.</summary>
    internal async Task ExpireAsync(WaitingRoomDefinition room, string key, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var roomId = room.Id!;
        List<string> missed;
        List<string> ended;
        using (var session = store.OpenAsyncSession())
        {
            missed = (await session.Query<WaitingRoomEntry>()
                    .Where(e => e.RoomId == roomId && e.Status == WaitingRoomEntryStatus.Called && e.PresenceDeadlineUtc < nowUtc)
                    .Take(Options.BatchSize).ToListAsync(cancellationToken))
                .Select(e => e.Id!).ToList();
            ended = (await session.Query<WaitingRoomEntry>()
                    .Where(e => e.RoomId == roomId && e.Status == WaitingRoomEntryStatus.Admitted && e.ActiveUntilUtc <= nowUtc)
                    .Take(Options.BatchSize).ToListAsync(cancellationToken))
                .Select(e => e.Id!).ToList();
        }
        if (missed.Count == 0 && ended.Count == 0)
            return;

        using var cluster = WaitingRoomStore.ClusterSession(store);
        var entries = await cluster.LoadAsync<WaitingRoomEntry>(missed.Concat(ended), cancellationToken);
        var changedToMissed = new List<WaitingRoomEntry>();
        foreach (var entry in entries.Values.OfType<WaitingRoomEntry>())
        {
            if (entry.Status == WaitingRoomEntryStatus.Called && entry.PresenceDeadlineUtc < nowUtc)
            {
                entry.Status = WaitingRoomEntryStatus.Missed;
                entry.Misses++;
                changedToMissed.Add(entry);
            }
            else if (entry.Status == WaitingRoomEntryStatus.Admitted && entry.ActiveUntilUtc <= nowUtc)
                entry.Status = WaitingRoomEntryStatus.Done;
        }
        if (!await SaveAsync(cluster, cancellationToken))
            return;
        foreach (var entry in changedToMissed)
        {
            publisher.PublishPrivate(key, entry.IdentityKey, new WaitingRoomPrivateFrame("missed", entry.Id!));
            await NotifyAsync("missed", room, entry, cancellationToken);
        }
    }

    /// <summary>Calls the next ranked entries within the room's limits; each gets a presence check (automatic while its tab is visible).</summary>
    internal async Task CallAsync(WaitingRoomDefinition room, string key, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var roomId = room.Id!;
        var slots = await SlotsAsync(room, key, nowUtc, cancellationToken);
        if (slots <= 0)
            return;

        List<string> next;
        using (var session = store.OpenAsyncSession())
            next = (await session.Query<WaitingRoomEntry>()
                    .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                    .Where(e => e.RoomId == roomId && e.Status == WaitingRoomEntryStatus.Waiting && !e.Shadow && e.Rank != null)
                    .OrderBy(e => e.Rank)
                    .Take(slots)
                    .ToListAsync(cancellationToken))
                .Select(e => e.Id!).ToList();
        if (next.Count == 0)
            return;

        using var cluster = WaitingRoomStore.ClusterSession(store);
        var state = await cluster.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
        if (state is null)
            return;
        RollMinute(state, nowUtc);
        var entries = await cluster.LoadAsync<WaitingRoomEntry>(next, cancellationToken);
        var called = new List<WaitingRoomEntry>();
        foreach (var id in next)
        {
            if (!entries.TryGetValue(id, out var entry) || entry is null || entry.Status != WaitingRoomEntryStatus.Waiting || entry.Rank is not { } rank)
                continue;
            entry.Status = WaitingRoomEntryStatus.Called;
            entry.CalledAtUtc = nowUtc;
            entry.PresenceDeadlineUtc = nowUtc + (entry.Hidden ? Options.HiddenPresenceWindow : Options.PresenceTimeout);
            state.ServedUpTo = Math.Max(state.ServedUpTo, rank);
            state.AdmittedThisMinute++;
            called.Add(entry);
        }
        if (called.Count == 0 || !await SaveAsync(cluster, cancellationToken))
            return;
        foreach (var entry in called)
        {
            publisher.PublishPrivate(key, entry.IdentityKey, new WaitingRoomPrivateFrame("called", entry.Id!, WaitingRoomStore.Utc(entry.PresenceDeadlineUtc)));
            await NotifyAsync("called", room, entry, cancellationToken);
        }
    }

    /// <summary>How many may be called now: below the maximum active, within the minute's rate, within projected capacity.</summary>
    internal async Task<int> SlotsAsync(WaitingRoomDefinition room, string key, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var roomId = room.Id!;
        int active;
        WaitingRoomState? state;
        using (var session = store.OpenAsyncSession())
        {
            active = await session.Query<WaitingRoomEntry>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(e => e.RoomId == roomId && (e.Status == WaitingRoomEntryStatus.Called || e.Status == WaitingRoomEntryStatus.Admitted))
                .CountAsync(cancellationToken);
            state = await session.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
        }
        if (state is null)
            return 0;
        var thisMinute = nowUtc - state.MinuteStartUtc >= TimeSpan.FromMinutes(1) ? 0 : state.AdmittedThisMinute;
        var slots = Math.Min(room.Admission.MaxActive - active, room.Admission.PerMinute - thisMinute);
        slots = Math.Min(slots, Options.BatchSize);
        if (await CapacityAsync(room, cancellationToken) is { } capacity)
        {
            // Admission is a right to try, not a reservation (research §3): admit only while what is left exceeds what
            // the visitors already inside are expected to take.
            var projected = capacity.Remaining - (long)active * room.Admission.UnitsPerAdmission;
            slots = (int)Math.Min(slots, Math.Max(0, projected / room.Admission.UnitsPerAdmission));
        }
        return slots;
    }

    private static void RollMinute(WaitingRoomState state, DateTime nowUtc)
    {
        if (nowUtc - state.MinuteStartUtc < TimeSpan.FromMinutes(1))
            return;
        state.MinuteStartUtc = nowUtc;
        state.AdmittedThisMinute = 0;
    }

    // ------------------------------------------------------------------ ballot

    /// <summary>
    /// A ballot: registration until the start; the draw at the start (the same seeded shuffle); then a window every
    /// <see cref="WaitingRoomBallotSettings.WindowLength"/>, each inviting the next group plus the places its predecessors
    /// left unused. An invitation is an admission with a token valid until its window ends (the personal single-use link).
    /// </summary>
    internal async Task BallotAsync(WaitingRoomDefinition room, string key, WaitingRoomState state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var drawAt = room.StartsAt.UtcDateTime + Options.SettleWindow;
        var nowUtc = now.UtcDateTime;
        if (nowUtc < drawAt)
            return;
        if (!state.Shuffled)
        {
            await ShuffleAsync(room, key, cancellationToken);
            state = await LoadStateAsync(key, cancellationToken) ?? state;
            if (!state.Shuffled)
                return;
        }

        await ExpireBallotAsync(room, key, nowUtc, cancellationToken);

        var window = (int)((nowUtc - drawAt).Ticks / room.Ballot.WindowLength.Ticks);
        using (var cluster = WaitingRoomStore.ClusterSession(store))
        {
            var current = await cluster.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
            if (current is not null && current.BallotWindowsOpened <= window)
            {
                current.BallotWindowsOpened = window + 1;
                current.BallotPending += room.Ballot.GroupSize + current.BallotCarry;
                current.BallotCarry = 0;
                await SaveAsync(cluster, cancellationToken);
            }
        }
        var windowEnd = drawAt + room.Ballot.WindowLength * (window + 1);
        await InviteAsync(room, key, nowUtc, windowEnd, cancellationToken);
    }

    private async Task ExpireBallotAsync(WaitingRoomDefinition room, string key, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var roomId = room.Id!;
        List<string> ended;
        using (var session = store.OpenAsyncSession())
            ended = (await session.Query<WaitingRoomEntry>()
                    .Where(e => e.RoomId == roomId && e.Status == WaitingRoomEntryStatus.Admitted && e.ActiveUntilUtc <= nowUtc)
                    .Take(Options.BatchSize).ToListAsync(cancellationToken))
                .Select(e => e.Id!).ToList();
        if (ended.Count == 0)
            return;

        using var cluster = WaitingRoomStore.ClusterSession(store);
        var state = await cluster.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
        if (state is null)
            return;
        foreach (var entry in (await cluster.LoadAsync<WaitingRoomEntry>(ended, cancellationToken)).Values.OfType<WaitingRoomEntry>())
        {
            if (entry.Status != WaitingRoomEntryStatus.Admitted || entry.ActiveUntilUtc > nowUtc)
                continue;
            entry.Status = WaitingRoomEntryStatus.Done;
            var spent = entry.TokenJti is { } jti && await claims.GetAsync(WaitingRoomAdmissions.Spent, WaitingRoomAdmissions.Spent.Key(jti), cancellationToken) is not null;
            if (!spent)
                state.BallotCarry++;
        }
        await SaveAsync(cluster, cancellationToken);
    }

    private async Task InviteAsync(WaitingRoomDefinition room, string key, DateTime nowUtc, DateTime windowEndUtc, CancellationToken cancellationToken)
    {
        var roomId = room.Id!;
        var state = await LoadStateAsync(key, cancellationToken);
        if (state is null || state.BallotPending <= 0 || windowEndUtc <= nowUtc)
            return;
        var take = Math.Min(state.BallotPending, Options.BatchSize);
        if (await CapacityAsync(room, cancellationToken) is { } capacity)
            take = (int)Math.Min(take, Math.Max(0, capacity.Remaining / room.Admission.UnitsPerAdmission));
        if (take <= 0)
            return; // nothing to sell right now; the pending invitations wait (they are not lost)

        List<string> next;
        using (var session = store.OpenAsyncSession())
            next = (await session.Query<WaitingRoomEntry>()
                    .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                    .Where(e => e.RoomId == roomId && e.Status == WaitingRoomEntryStatus.Waiting && !e.Shadow && e.Rank != null)
                    .OrderBy(e => e.Rank)
                    .Take(take)
                    .ToListAsync(cancellationToken))
                .Select(e => e.Id!).ToList();

        using var cluster = WaitingRoomStore.ClusterSession(store);
        var current = await cluster.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
        if (current is null)
            return;
        var invited = new List<WaitingRoomEntry>();
        if (next.Count == 0)
            current.BallotPending = 0; // nobody left to invite
        else
        {
            var entries = await cluster.LoadAsync<WaitingRoomEntry>(next, cancellationToken);
            foreach (var id in next)
            {
                if (!entries.TryGetValue(id, out var entry) || entry is null || entry.Status != WaitingRoomEntryStatus.Waiting || entry.Rank is not { } rank)
                    continue;
                var lifetime = windowEndUtc - nowUtc;
                WaitingRoomStore.Admit(entry, room, tokens, nowUtc, lifetime, await WaitingRoomStore.ShouldPoisonAsync(services, entry, cancellationToken));
                current.ServedUpTo = Math.Max(current.ServedUpTo, rank);
                current.BallotPending--;
                invited.Add(entry);
            }
        }
        if (!await SaveAsync(cluster, cancellationToken))
            return;
        foreach (var entry in invited)
        {
            publisher.PublishPrivate(key, entry.IdentityKey, new WaitingRoomPrivateFrame("admitted", entry.Id!, null, entry.Token,
                WaitingRoomStore.Utc(entry.ActiveUntilUtc), WaitingRoomStore.AdmittedUrl(room, entry.Token)));
            await NotifyAsync("admitted", room, entry, cancellationToken);
        }
    }

    // ------------------------------------------------------------------ capacity and closing

    private async Task<WaitingRoomCapacity?> CapacityAsync(WaitingRoomDefinition room, CancellationToken cancellationToken)
    {
        if (room.CapacityKey is not { Length: > 0 } capacityKey)
            return null;
        foreach (var source in services.GetServices<IWaitingRoomCapacity>())
        {
            try
            {
                if (await source.GetAsync(capacityKey, cancellationToken) is { } capacity)
                    return capacity;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "{Source} failed to read capacity {Key}.", source.GetType().Name, capacityKey);
            }
        }
        return null;
    }

    /// <summary>
    /// Truthful sold-out handling (§4.12): no capacity and no outstanding holds closes the room for good; no capacity but
    /// holds outstanding says stock may come back and admits nobody; capacity again reopens it.
    /// </summary>
    private async Task CheckCapacityAsync(WaitingRoomDefinition room, string key, WaitingRoomState state, CancellationToken cancellationToken)
    {
        if (await CapacityAsync(room, cancellationToken) is not { } capacity || state.Status == WaitingRoomStatus.Closed)
            return;
        var status = capacity.Remaining > 0 ? WaitingRoomStatus.Open
            : capacity.OutstandingHolds > 0 ? WaitingRoomStatus.MayReturn
            : WaitingRoomStatus.SoldOut;
        if (status == state.Status || (status == WaitingRoomStatus.Open && state.Status is WaitingRoomStatus.Scheduled or WaitingRoomStatus.PreQueue))
            return;
        using var cluster = WaitingRoomStore.ClusterSession(store);
        var current = await cluster.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
        if (current is null)
            return;
        current.Status = status;
        await SaveAsync(cluster, cancellationToken);
    }

    /// <summary>Persists a closed or sold-out status; a ballot's undrawn entries learn they were not drawn.</summary>
    private async Task CloseAsync(WaitingRoomDefinition room, string key, string status, CancellationToken cancellationToken)
    {
        var roomId = room.Id!;
        using (var cluster = WaitingRoomStore.ClusterSession(store))
        {
            var current = await cluster.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
            if (current is not null && current.Status != status && status == WaitingRoomStatus.Closed)
            {
                current.Status = status;
                await SaveAsync(cluster, cancellationToken);
            }
        }
        if (room.Mode != WaitingRoomMode.Ballot)
            return;

        List<string> waiting;
        using (var session = store.OpenAsyncSession())
            waiting = (await session.Query<WaitingRoomEntry>()
                    .Where(e => e.RoomId == roomId && e.Status == WaitingRoomEntryStatus.Waiting)
                    .Take(Options.BatchSize).ToListAsync(cancellationToken))
                .Select(e => e.Id!).ToList();
        if (waiting.Count == 0)
            return;
        using var lost = WaitingRoomStore.ClusterSession(store);
        var entries = (await lost.LoadAsync<WaitingRoomEntry>(waiting, cancellationToken)).Values.OfType<WaitingRoomEntry>()
            .Where(e => e.Status == WaitingRoomEntryStatus.Waiting).ToList();
        foreach (var entry in entries)
        {
            WaitingRoomStore.CaptureArrival(lost, entry);
            entry.Status = WaitingRoomEntryStatus.Lost;
        }
        if (!await SaveAsync(lost, cancellationToken))
            return;
        foreach (var entry in entries)
        {
            publisher.PublishPrivate(WaitingRoomStore.KeyOf(roomId), entry.IdentityKey, new WaitingRoomPrivateFrame("lost", entry.Id!));
            await NotifyAsync("lost", room, entry, cancellationToken);
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<WaitingRoomState?> LoadStateAsync(string key, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key), cancellationToken);
    }

    /// <summary>Commits a cluster-wide session; false when another writer changed what it read (nothing was written).</summary>
    private async Task<bool> SaveAsync(IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (ConcurrencyException)
        {
            logger.LogDebug("A waiting-room write conflicted with another writer; the next tick reads again.");
            return false;
        }
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
