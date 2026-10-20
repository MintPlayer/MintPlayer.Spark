using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Challenges.Contracts;

namespace MintPlayer.Spark.WaitingRoom.Contracts;

// The responses @mintplayer/ng-spark/waiting-room consumes (X20.2). Modes and statuses travel as strings.

/// <summary>A room as its page shows it (<c>GET /spark/waiting-room/{key}</c>).</summary>
/// <param name="Mode"><c>RandomizedPreQueue</c>, <c>Ballot</c> or <c>Fifo</c>.</param>
/// <param name="Status">A <c>WaitingRoomStatus</c> value.</param>
/// <param name="SeedCommitment">SHA-256 of the shuffle or draw seed, published before joining closes.</param>
/// <param name="Seed">The seed itself, once revealed (after the shuffle or the draw).</param>
/// <param name="JoinChallenge">Whether joining asks a challenge now.</param>
[SparkContract]
public sealed record WaitingRoomInfo(
    string Key,
    TranslatedString? Title,
    string Mode,
    string Status,
    DateTimeOffset? OpensAt,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool RequireSignIn,
    long ServedUpTo,
    long QueueLength,
    string SeedCommitment,
    string? Seed,
    bool JoinChallenge);

/// <summary>The caller's own entry.</summary>
/// <param name="Status">A <c>WaitingRoomEntryStatus</c> value.</param>
/// <param name="Rank">The dense rank; null until ranked (before the shuffle, or for a moment after joining).</param>
/// <param name="Ahead">How many are ahead (<c>rank − servedUpTo − 1</c>, at least 0); null until ranked.</param>
/// <param name="PresenceDeadline">While called: the deadline of the presence check.</param>
/// <param name="AdmissionToken">While admitted: the single-use admission token (also on the private stream).</param>
/// <param name="AdmittedUrl">While admitted: the room's link for the admitted, with the token filled in.</param>
[SparkContract]
public sealed record WaitingRoomEntryView(
    string EntryId,
    string RoomKey,
    string Status,
    long? Rank,
    long? Ahead,
    bool PreQueue,
    DateTimeOffset? PresenceDeadline,
    string? AdmissionToken,
    DateTimeOffset? AdmissionExpiresAt,
    string? AdmittedUrl,
    int Misses);

/// <summary>What a join answered (<c>POST /spark/waiting-room/{key}/join</c>).</summary>
/// <param name="Outcome"><c>joined</c>, <c>challenge</c> (solve <paramref name="Challenge"/> and join again), <c>sign-in-required</c>,
/// <c>phone-verification-required</c>, <c>account-too-new</c>, <c>not-open</c>, <c>registration-closed</c>, <c>sold-out</c>,
/// <c>closed</c>, <c>slow-down</c> (come back after <paramref name="RetryAfterSeconds"/>), <c>refused</c> (deterministic evidence; quote <paramref name="DecisionId"/> to appeal).</param>
[SparkContract]
public sealed record WaitingRoomJoinResult(
    string Outcome,
    WaitingRoomEntryView? Entry = null,
    SparkChallengeOffer? Challenge = null,
    int? RetryAfterSeconds = null,
    string? DecisionId = null);

/// <summary>A join (<c>POST …/join</c>).</summary>
/// <param name="Challenge">A solved challenge, when the previous answer asked for one.</param>
/// <param name="Website">The honeypot: a hidden field a person never fills (leave it empty).</param>
/// <param name="Webdriver">The client's <c>navigator.webdriver</c>, as weak evidence.</param>
[SparkContract]
public sealed record WaitingRoomJoinBody(SparkChallengeResponse? Challenge = null, string? Website = null, bool? Webdriver = null);

/// <summary>A sign of life (<c>POST …/presence</c>): answers a presence check, brings a missed entry back at its rank.</summary>
/// <param name="Visible">Whether the tab is visible (Page Visibility); a hidden tab gets the longer window.</param>
[SparkContract]
public sealed record WaitingRoomPresenceBody(bool Visible = true);

/// <summary>The public topic <c>waiting-room:{key}</c>: the same for everyone, no per-viewer read (X2). Frames carry <c>serverTime</c>.</summary>
[SparkContract]
public sealed record WaitingRoomFrame(
    string Key,
    string Status,
    long ServedUpTo,
    long QueueLength,
    string SeedCommitment,
    string? Seed);

/// <summary>
/// The private stream <c>waiting-room:{key}</c> of one identity.
/// </summary>
/// <param name="Kind"><c>called</c> (answer the presence check by <paramref name="Deadline"/>), <c>admitted</c>, <c>missed</c>, <c>lost</c>.</param>
[SparkContract]
public sealed record WaitingRoomPrivateFrame(
    string Kind,
    string EntryId,
    DateTimeOffset? Deadline = null,
    string? AdmissionToken = null,
    DateTimeOffset? AdmissionExpiresAt = null,
    string? AdmittedUrl = null);

/// <summary>
/// The proof of a shuffle or draw (<c>GET …/draw</c>): anyone can check <c>SHA-256(seed) = commitment</c>, then recompute
/// the order from <paramref name="EntryIds"/> with <paramref name="Algorithm"/> and find their own entry's rank.
/// </summary>
/// <param name="Algorithm"><c>spark-shuffle-v1</c>: Fisher–Yates over the ids sorted ordinally, randomness from HMAC-SHA256(seed ‖ inputHash, counter).</param>
/// <param name="EntryIds">The ids the order was drawn over, sorted ordinally; omitted above 50,000 (then <paramref name="Count"/> only).</param>
[SparkContract]
public sealed record WaitingRoomDrawProof(
    string Algorithm,
    string SeedCommitment,
    string? Seed,
    string? InputHash,
    long Count,
    IReadOnlyList<string>? EntryIds);
