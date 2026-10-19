using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Challenges.Deceptive;
using MintPlayer.Spark.Challenges.Risk;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.WaitingRoom.Admissions;
using MintPlayer.Spark.WaitingRoom.Contracts;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.WaitingRoom.Services;

/// <summary>Keys, sessions and the operations the room service, the worker and the merge share.</summary>
internal static partial class WaitingRoomStore
{
    public const string RoomPrefix = "WaitingRooms/";
    public const string EntryPrefix = "WaitingRoomEntries/";

    /// <summary>One entry per identity per room: <c>waitingroom/identities/{key}/{identityKey}</c>, owned by the identity, holding the entry id.</summary>
    public static readonly SparkClaimKind<SparkClaimHold> Identities = SparkClaimKinds.Holds("waitingroom/identities/");

    /// <summary>The admission worker's lease per room.</summary>
    public static readonly SparkClaimKind<SparkClaimHold> Leases = SparkClaimKinds.Holds("waitingroom/leases/");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex KeyPattern();

    /// <summary>A room key: lower-case letters, digits, <c>. _ -</c>, at most 100. It names compare-exchange keys and topics.</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public static string RoomId(string key) => RoomPrefix + key;

    public static string StateId(string key) => RoomPrefix + key + "/state";

    public static string KeyOf(string roomId) => roomId.StartsWith(RoomPrefix, StringComparison.Ordinal) ? roomId[RoomPrefix.Length..] : roomId;

    public static SparkClaimKey IdentityKey(string roomKey, string identityKey) => Identities.Key(roomKey + "/" + identityKey);

    public static IAsyncDocumentSession ClusterSession(IDocumentStore store)
        => store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });

    /// <summary>The room's status at <paramref name="now"/>: the schedule, then what the worker found (sold out, may return).</summary>
    public static string StatusAt(WaitingRoomDefinition room, WaitingRoomState? state, DateTimeOffset now)
    {
        if (state?.Status is WaitingRoomStatus.SoldOut or WaitingRoomStatus.Closed)
            return state.Status;
        if (room.EndsAt is { } ends && now >= ends)
            return WaitingRoomStatus.Closed;
        if (room.OpensAt is { } opens && now < opens)
            return WaitingRoomStatus.Scheduled;
        if (now < room.StartsAt)
            return WaitingRoomStatus.PreQueue;
        return state?.Status == WaitingRoomStatus.MayReturn ? WaitingRoomStatus.MayReturn : WaitingRoomStatus.Open;
    }

    public static WaitingRoomEntryView ToView(WaitingRoomEntry entry, WaitingRoomDefinition room, WaitingRoomState? state)
    {
        var servedUpTo = state?.ServedUpTo ?? 0;
        long? ahead = entry.Rank is { } rank ? Math.Max(0, rank - servedUpTo - 1) : null;
        var status = entry.Status;
        if (entry.Shadow)
        {
            // A shadow entry looks queued and close to its turn, and never gets there (deterministic evidence only, logged).
            status = WaitingRoomEntryStatus.Waiting;
            ahead = ahead is null ? null : Math.Max(1, ahead.Value);
        }
        else if (status is WaitingRoomEntryStatus.Called or WaitingRoomEntryStatus.Admitted)
            ahead = 0;
        var admitted = status == WaitingRoomEntryStatus.Admitted && !entry.Shadow;
        return new WaitingRoomEntryView(
            entry.Id!,
            KeyOf(room.Id!),
            status,
            entry.Rank,
            ahead,
            entry.PreQueue,
            status == WaitingRoomEntryStatus.Called ? Utc(entry.PresenceDeadlineUtc) : null,
            admitted ? entry.Token : null,
            admitted ? Utc(entry.ActiveUntilUtc) : null,
            admitted ? AdmittedUrl(room, entry.Token) : null,
            entry.Misses);
    }

    public static string? AdmittedUrl(WaitingRoomDefinition room, string? token)
        => room.AdmittedUrl is { Length: > 0 } url && token is not null ? url.Replace("{token}", Uri.EscapeDataString(token), StringComparison.Ordinal) : null;

    public static DateTimeOffset? Utc(DateTime? value) => value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    /// <summary>Admits <paramref name="entry"/>: a fresh single-use token bound to its identity, the room and the room's gate.</summary>
    public static void Admit(WaitingRoomEntry entry, WaitingRoomDefinition room, WaitingRoomTokens tokens, DateTime nowUtc, TimeSpan lifetime, bool poisoned)
    {
        var expires = nowUtc + lifetime;
        var (token, jti) = tokens.Issue(room.Id!, room.Gate, entry.Id!, entry.IdentityKey, new DateTimeOffset(expires, TimeSpan.Zero), poisoned);
        entry.Status = WaitingRoomEntryStatus.Admitted;
        entry.AdmittedAtUtc = nowUtc;
        entry.ActiveUntilUtc = expires;
        entry.PresenceDeadlineUtc = null;
        entry.Token = token;
        entry.TokenJti = jti;
        entry.Poisoned = poisoned;
    }

    /// <summary>
    /// Whether this identity's admission token is issued poisoned: the deceptive measure is on and deterministic evidence
    /// was recorded against the identity (a replayed or forged token). Logged when it applies.
    /// </summary>
    public static async Task<bool> ShouldPoisonAsync(IServiceProvider services, WaitingRoomEntry entry, CancellationToken cancellationToken)
    {
        if (services.GetService<ISparkDeceptiveMeasures>() is not { AnyEnabled: true } deceptive || services.GetService<ISparkRiskPipeline>() is not { } pipeline)
            return false;
        var recorded = pipeline.RecordedAgainst(entry.IdentityKey);
        if (recorded.Count == 0)
            return false;
        var decision = new SparkRiskDecision(SparkRiskAction.Block, 1, recorded);
        return await deceptive.ApplyAsync(SparkDeceptiveMeasureNames.PoisonedTokens,
            new SparkRiskContext("admission", entry.IdentityKey, "", entry.RoomId), decision, cancellationToken);
    }

    /// <summary>Captures the entry's arrival (its creation <c>@last-modified</c>) before a write moves it.</summary>
    public static void CaptureArrival(IAsyncDocumentSession session, WaitingRoomEntry entry)
    {
        if (entry.ArrivedAtUtc is not null)
            return;
        entry.ArrivedAtUtc = LastModified(session, entry);
    }

    public static DateTime LastModified(IAsyncDocumentSession session, WaitingRoomEntry entry)
    {
        var metadata = session.Advanced.GetMetadataFor(entry);
        return metadata.TryGetValue(Raven.Client.Constants.Documents.Metadata.LastModified, out var value)
            && DateTime.TryParse(value?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTime.MaxValue;
    }

    /// <summary>The frame of the public topic.</summary>
    public static WaitingRoomFrame Frame(WaitingRoomDefinition room, WaitingRoomState state, DateTimeOffset now)
        => new(KeyOf(room.Id!), StatusAt(room, state, now), state.ServedUpTo, Math.Max(0, state.RankedCount - state.ServedUpTo),
            state.SeedCommitment, state.SeedRevealedAtUtc is null ? null : state.Seed);
}
