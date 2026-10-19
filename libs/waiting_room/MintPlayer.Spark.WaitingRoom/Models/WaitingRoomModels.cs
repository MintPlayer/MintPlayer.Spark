using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.WaitingRoom;

/// <summary>How a room orders its queue (D10).</summary>
public enum WaitingRoomMode
{
    /// <summary>
    /// The default. Everyone who joins before the sale starts is shuffled at the start with a committed seed; later
    /// arrivals queue first-come behind them. Arriving early buys nothing, so a script that floods at T−1 s gains nothing.
    /// </summary>
    RandomizedPreQueue,

    /// <summary>Registration, then a provably fair draw (seed committed before registration closes, revealed after), then personal single-use purchase links in staged windows; unused places roll to the next group.</summary>
    Ballot,

    /// <summary>First come, first served, ordered by the server's commit time (<c>@last-modified</c>, captured once: 0 inversions measured, research §3.6).</summary>
    Fifo,
}

/// <summary>
/// A waiting room (commerce PRD §4.12): bound to a gate (a sale phase, or any route), with a schedule and a mode. Stored
/// as <c>WaitingRooms/{key}</c>; created by code (<see cref="ISparkWaitingRooms.SaveRoomAsync"/>, e.g. ticketing per sale
/// phase) or from configuration (<c>Spark:WaitingRoom:Rooms</c>).
/// </summary>
public sealed class WaitingRoomDefinition
{
    public string? Id { get; set; }

    /// <summary>What the room page shows as its title.</summary>
    public TranslatedString? Title { get; set; }

    /// <summary>
    /// The gate the admission tokens are bound to: a sale phase id (<c>SalePhases/42</c>) or a route the host protects.
    /// A hold or checkout for the gate refuses requests without a valid token (<see cref="IWaitingRoomAdmissions"/>).
    /// </summary>
    public string Gate { get; set; } = "";

    public WaitingRoomMode Mode { get; set; } = WaitingRoomMode.RandomizedPreQueue;

    /// <summary>When joining opens: the pre-queue (RandomizedPreQueue), registration (Ballot) or the queue (Fifo). Null: open now.</summary>
    public DateTimeOffset? OpensAt { get; set; }

    /// <summary>When the sale starts: the shuffle (RandomizedPreQueue), the draw and the first window (Ballot), the first admission.</summary>
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>When the sale ends; the room closes. Null: it closes only when sold out.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Only signed-in visitors may join. Required for RandomizedPreQueue and Ballot unless <see cref="AllowGuestsInFairModes"/> (D10).</summary>
    public bool RequireSignIn { get; set; }

    /// <summary>
    /// Allows guests in a RandomizedPreQueue or Ballot room (D10's override). Without sign-in one person is many
    /// identities, so a shuffle or a draw rewards whoever opens the most browsers.
    /// </summary>
    public bool AllowGuestsInFairModes { get; set; }

    /// <summary>Only accounts with a verified phone number (X13) may join (D29 opt-in). Implies sign-in.</summary>
    public bool RequireVerifiedPhone { get; set; }

    /// <summary>Only accounts at least this old (X11) may join (D29 opt-in). Implies sign-in.</summary>
    public TimeSpan? MinimumAccountAge { get; set; }

    /// <summary>Ask a challenge at join (once per identity), whether or not the <c>queue-join</c> point is switched on globally.</summary>
    public bool ChallengeAtJoin { get; set; }

    public WaitingRoomAdmissionSettings Admission { get; set; } = new();

    /// <summary>Ballot only.</summary>
    public WaitingRoomBallotSettings Ballot { get; set; } = new();

    /// <summary>What <see cref="IWaitingRoomCapacity"/> is asked about (a quota id, an event date); null: the room never sells out by itself.</summary>
    public string? CapacityKey { get; set; }

    /// <summary>Where an admitted visitor goes; <c>{token}</c> is replaced by the admission token (the personal single-use link).</summary>
    public string? AdmittedUrl { get; set; }
}

/// <summary>How fast a room admits (§4.12 "Admission").</summary>
public sealed class WaitingRoomAdmissionSettings
{
    /// <summary>The most visitors admitted (or called) at once. Default 200.</summary>
    public int MaxActive { get; set; } = 200;

    /// <summary>The most admissions per minute. Default 120.</summary>
    public int PerMinute { get; set; } = 120;

    /// <summary>
    /// How long an admission counts as active and its token lives: at least the hold plus the payment window (§4.12).
    /// Default 30 minutes.
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The units one admitted visitor is expected to take, for the projected-capacity check. Default 1.</summary>
    public int UnitsPerAdmission { get; set; } = 1;
}

/// <summary>A ballot's staged invitations.</summary>
public sealed class WaitingRoomBallotSettings
{
    /// <summary>How many ranked entries one window invites, before unused places roll over. Default 100.</summary>
    public int GroupSize { get; set; } = 100;

    /// <summary>How long each window lasts; an invitation (its link) is valid for its window. Default 30 minutes.</summary>
    public TimeSpan WindowLength { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>The status of a room, as the public topic and the page show it.</summary>
public static class WaitingRoomStatus
{
    /// <summary>Not open yet.</summary>
    public const string Scheduled = "scheduled";
    /// <summary>Joining is open before the sale (the pre-queue, or a ballot's registration).</summary>
    public const string PreQueue = "pre-queue";
    /// <summary>The sale runs and visitors are admitted.</summary>
    public const string Open = "open";
    /// <summary>No units left right now, but holds are outstanding: stock may come back (§4.12 "Sold out").</summary>
    public const string MayReturn = "may-return";
    /// <summary>Sold out for good: no capacity and no outstanding holds.</summary>
    public const string SoldOut = "sold-out";
    /// <summary>The sale ended.</summary>
    public const string Closed = "closed";
}

/// <summary>An entry's status.</summary>
public static class WaitingRoomEntryStatus
{
    /// <summary>In the queue (ranked or waiting for its rank).</summary>
    public const string Waiting = "waiting";
    /// <summary>Its turn: the presence check is running.</summary>
    public const string Called = "called";
    /// <summary>Admitted: holds an admission token.</summary>
    public const string Admitted = "admitted";
    /// <summary>Missed the presence check; keeps its rank and rejoins at it on its next sign of life (WCAG 2.2.1).</summary>
    public const string Missed = "missed";
    /// <summary>The admission window ended.</summary>
    public const string Done = "done";
    /// <summary>A ballot entry that was not drawn before the sale ended or sold out.</summary>
    public const string Lost = "lost";
    /// <summary>Left the queue, or merged into another entry of the same person.</summary>
    public const string Left = "left";
}

/// <summary>A room's working state, written by the admission worker (and the shuffle) only: <c>WaitingRooms/{key}/state</c>.</summary>
public sealed class WaitingRoomState
{
    public string? Id { get; set; }

    public string RoomId { get; set; } = "";

    public string Status { get; set; } = WaitingRoomStatus.Scheduled;

    /// <summary>How many entries have a rank (ranks are dense: 1..RankedCount).</summary>
    public long RankedCount { get; set; }

    /// <summary>The highest rank called or invited so far. A client shows <c>rank − servedUpTo</c>.</summary>
    public long ServedUpTo { get; set; }

    /// <summary>The seed's SHA-256 (hex), published before joining closes.</summary>
    public string SeedCommitment { get; set; } = "";

    /// <summary>The seed (hex). Secret until <see cref="SeedRevealedAtUtc"/>, then published so anyone can check the order.</summary>
    public string Seed { get; set; } = "";

    public DateTime? SeedRevealedAtUtc { get; set; }

    /// <summary>The SHA-256 of the sorted entry ids the shuffle or draw ran over (hex); part of its proof.</summary>
    public string? DrawInputHash { get; set; }

    /// <summary>Whether the start-of-sale shuffle (or the ballot draw) is done.</summary>
    public bool Shuffled { get; set; }

    /// <summary>How many entries the shuffle or draw ranked (ranks 1..ShuffledCount); later ranks are first-come latecomers.</summary>
    public long ShuffledCount { get; set; }

    /// <summary>The admissions of the current minute (the rate limit).</summary>
    public int AdmittedThisMinute { get; set; }

    public DateTime MinuteStartUtc { get; set; }

    /// <summary>Ballot: the windows already opened.</summary>
    public int BallotWindowsOpened { get; set; }

    /// <summary>Ballot: places of expired, unused invitations still to roll into the next window.</summary>
    public int BallotCarry { get; set; }

    /// <summary>Ballot: invitations the current window still has to send.</summary>
    public int BallotPending { get; set; }

    public DateTime? LastTickUtc { get; set; }
}

/// <summary>
/// One person's place in one room: <c>WaitingRoomEntries/{random}</c>. One per identity per room, guarded by the
/// compare-exchange key <c>waitingroom/identities/{roomId}/{identityKey}</c>, so a refresh, another tab or another
/// device returns the same entry.
/// </summary>
public sealed class WaitingRoomEntry : IExpiring
{
    public string? Id { get; set; }

    public string RoomId { get; set; } = "";

    /// <summary><c>user:{id}</c> or <c>visitor:{id}</c>.</summary>
    public string IdentityKey { get; set; } = "";

    public string? UserId { get; set; }

    public string? VisitorId { get; set; }

    /// <summary>
    /// The server time it joined: its <c>@last-modified</c> at creation, captured by the first write that changes the
    /// entry (the ranker, a merge), so later writes do not move it.
    /// </summary>
    public DateTime? ArrivedAtUtc { get; set; }

    /// <summary>Joined before the sale started (shuffled with the pre-queue).</summary>
    public bool PreQueue { get; set; }

    /// <summary>Dense rank, assigned by the ranker; null until then.</summary>
    public long? Rank { get; set; }

    public string Status { get; set; } = WaitingRoomEntryStatus.Waiting;

    /// <summary>A shadow entry (deceptive measure, deterministic evidence only): looks queued, is never called.</summary>
    public bool Shadow { get; set; }

    /// <summary>The client last reported its tab hidden (Page Visibility): the presence check then waits the longer window.</summary>
    public bool Hidden { get; set; }

    public DateTime? LastSeenUtc { get; set; }

    public DateTime? CalledAtUtc { get; set; }

    public DateTime? PresenceDeadlineUtc { get; set; }

    /// <summary>How many presence checks it missed (it never loses its rank for it).</summary>
    public int Misses { get; set; }

    public DateTime? AdmittedAtUtc { get; set; }

    /// <summary>Until when the admission counts as active (and its token lives).</summary>
    public DateTime? ActiveUntilUtc { get; set; }

    /// <summary>The admission token, so every device of the identity gets the same one (it is single-use regardless).</summary>
    public string? Token { get; set; }

    public string? TokenJti { get; set; }

    /// <summary>The token was issued poisoned (deceptive measure).</summary>
    public bool Poisoned { get; set; }

    /// <summary>Passed a join challenge (never asked again for this entry).</summary>
    public bool ChallengePassed { get; set; }

    /// <summary>When it merged into another entry of the same person: that entry.</summary>
    public string? MergedInto { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
}
