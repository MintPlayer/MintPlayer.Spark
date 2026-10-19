using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.WaitingRoom.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.WaitingRoom.Identity;

/// <summary>
/// One entry per identity survives sign-in (X1, S16): a guest's entries move onto the account they signed in to. When
/// the account already has an entry in the same room, the <b>better</b> one is kept (admitted before called before
/// waiting; then the lower rank; then the earlier arrival) and the other is marked left, merged into it. Runs inside the
/// visitor merge's cluster-wide transaction, writing the identity keys through its cluster transaction.
/// </summary>
/// <remarks>
/// An admission token issued to the visitor stays valid for the account: <see cref="IWaitingRoomAdmissions"/> accepts a
/// visitor subject whose merge record names the presenting user.
/// </remarks>
internal sealed class WaitingRoomVisitorMerge : ISparkVisitorMergeHandler
{
    public async Task MergeAsync(string visitorId, string userId, IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        var visitorKey = "visitor:" + visitorId;
        var userKey = "user:" + userId;
        var entries = await session.Query<WaitingRoomEntry>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(e => e.IdentityKey == visitorKey && e.Status != WaitingRoomEntryStatus.Left)
            .Take(256)
            .ToListAsync(cancellationToken);

        foreach (var guest in entries)
        {
            var roomKey = WaitingRoomStore.KeyOf(guest.RoomId);
            var guestClaim = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<SparkClaimHold>(
                WaitingRoomStore.IdentityKey(roomKey, visitorKey).Value, cancellationToken);
            var userClaim = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<SparkClaimHold>(
                WaitingRoomStore.IdentityKey(roomKey, userKey).Value, cancellationToken);
            var account = userClaim?.Value?.Data is { } accountEntryId
                ? await session.LoadAsync<WaitingRoomEntry>(accountEntryId, cancellationToken)
                : null;

            WaitingRoomStore.CaptureArrival(session, guest);
            if (account is not null && account.Status != WaitingRoomEntryStatus.Left)
            {
                WaitingRoomStore.CaptureArrival(session, account);
                var keep = Better(guest, account, session);
                var drop = ReferenceEquals(keep, guest) ? account : guest;
                drop.Status = WaitingRoomEntryStatus.Left;
                drop.MergedInto = keep.Id;
                if (ReferenceEquals(keep, guest))
                {
                    MoveToAccount(guest, userKey, userId);
                    userClaim!.Value = new SparkClaimHold(userKey, null, guest.Id);
                }
            }
            else
            {
                MoveToAccount(guest, userKey, userId);
                if (userClaim is null)
                    session.Advanced.ClusterTransaction.CreateCompareExchangeValue(WaitingRoomStore.IdentityKey(roomKey, userKey).Value, new SparkClaimHold(userKey, null, guest.Id));
                else
                    userClaim.Value = new SparkClaimHold(userKey, null, guest.Id);
            }
            if (guestClaim is not null)
                session.Advanced.ClusterTransaction.DeleteCompareExchangeValue(guestClaim.Key, guestClaim.Index);
        }
    }

    private static void MoveToAccount(WaitingRoomEntry entry, string userKey, string userId)
    {
        entry.IdentityKey = userKey;
        entry.UserId = userId;
    }

    /// <summary>The entry to keep: further along first, then the lower rank, then the earlier arrival.</summary>
    internal static WaitingRoomEntry Better(WaitingRoomEntry a, WaitingRoomEntry b, IAsyncDocumentSession? session = null)
    {
        var ra = Progress(a);
        var rb = Progress(b);
        if (ra != rb)
            return ra > rb ? a : b;
        if (a.Rank is { } x && b.Rank is { } y && x != y)
            return x < y ? a : b;
        if (a.Rank is not null != b.Rank is not null)
            return a.Rank is not null ? a : b;
        var arrivedA = a.ArrivedAtUtc ?? (session is null ? DateTime.MaxValue : WaitingRoomStore.LastModified(session, a));
        var arrivedB = b.ArrivedAtUtc ?? (session is null ? DateTime.MaxValue : WaitingRoomStore.LastModified(session, b));
        return arrivedA <= arrivedB ? a : b;
    }

    private static int Progress(WaitingRoomEntry entry) => entry.Status switch
    {
        WaitingRoomEntryStatus.Admitted => 4,
        WaitingRoomEntryStatus.Called => 3,
        WaitingRoomEntryStatus.Waiting or WaitingRoomEntryStatus.Missed => 2,
        WaitingRoomEntryStatus.Done => 1,
        _ => 0,
    };
}
