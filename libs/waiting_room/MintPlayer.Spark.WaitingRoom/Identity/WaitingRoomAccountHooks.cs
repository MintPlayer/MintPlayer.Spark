using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.WaitingRoom.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.WaitingRoom.Identity;

/// <summary>
/// GDPR Art. 15/20: the account's waiting-room entries (room, status, rank, times). The admission token is left out: it
/// is a bearer credential, not data about the person, and it is useless once its short lifetime ends.
/// </summary>
internal sealed class WaitingRoomPersonalData<TUser>(IDocumentStore store) : ISparkPersonalDataContributor<TUser> where TUser : SparkUser
{
    public string Name => "waiting-room";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        using var session = store.OpenAsyncSession();
        var entries = await session.Query<WaitingRoomEntry>()
            .Where(e => e.UserId == userId)
            .Take(1024)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
            return null;
        return entries.Select(e => new
        {
            Room = WaitingRoomStore.KeyOf(e.RoomId),
            e.Status,
            e.Rank,
            e.PreQueue,
            e.ArrivedAtUtc,
            e.LastSeenUtc,
            e.CalledAtUtc,
            e.AdmittedAtUtc,
            e.ActiveUntilUtc,
            e.Misses,
            e.ChallengePassed,
            e.MergedInto,
        });
    }
}

/// <summary>
/// GDPR Art. 17: the account's entries are anonymised and its identity keys released, in one cluster-wide transaction
/// per batch (the identity keys are compare-exchange values). An entry is anonymised, not deleted, because a
/// RandomizedPreQueue or Ballot draw proof lists the ranked entry ids (<see cref="ISparkWaitingRooms.DrawProofAsync"/>):
/// deleting one would make the published proof unverifiable. The anonymised entry keeps only its random id, rank and
/// times, and expires with the room's other entries. An active entry leaves the queue; an unspent token stays sealed to
/// an identity that no longer exists, so it can no longer be presented.
/// </summary>
/// <remarks>
/// Idempotent (a retried deletion runs it again): an anonymised entry no longer matches the user id. Evidence the risk
/// pipeline recorded against <c>user:{id}</c> lives in memory only and lapses after
/// <c>Spark:Challenges:Risk:RecordedEvidenceLifetime</c>.
/// </remarks>
internal sealed class WaitingRoomAccountDeletion<TUser>(IDocumentStore store) : ISparkAccountDeletionHandler<TUser> where TUser : SparkUser
{
    private const int BatchSize = 256;

    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        while (true)
        {
            using var session = WaitingRoomStore.ClusterSession(store);
            var entries = await session.Query<WaitingRoomEntry>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(e => e.UserId == userId)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (entries.Count == 0)
                return;
            foreach (var entry in entries)
                await EraseAsync(session, entry, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            if (entries.Count < BatchSize)
                return;
        }
    }

    internal static async Task EraseAsync(IAsyncDocumentSession session, WaitingRoomEntry entry, CancellationToken cancellationToken)
    {
        var roomKey = WaitingRoomStore.KeyOf(entry.RoomId);
        var claim = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<SparkClaimHold>(
            WaitingRoomStore.IdentityKey(roomKey, entry.IdentityKey).Value, cancellationToken);
        if (claim is not null && string.Equals(claim.Value?.Data, entry.Id, StringComparison.Ordinal))
            session.Advanced.ClusterTransaction.DeleteCompareExchangeValue(claim.Key, claim.Index);

        WaitingRoomStore.CaptureArrival(session, entry);
        if (entry.Status is WaitingRoomEntryStatus.Waiting or WaitingRoomEntryStatus.Called or WaitingRoomEntryStatus.Admitted or WaitingRoomEntryStatus.Missed)
            entry.Status = WaitingRoomEntryStatus.Left;
        entry.IdentityKey = "erased:" + entry.Id;
        entry.UserId = null;
        entry.VisitorId = null;
        entry.Token = null;
        entry.Hidden = false;
        entry.PresenceDeadlineUtc = null;
    }
}
