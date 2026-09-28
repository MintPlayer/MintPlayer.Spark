using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Moderation.Documents;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// GDPR (D8): what Moderation holds about an account when it is deleted. Every vote the account cast
/// is reversed first (fraud measure 7: "deleting an account reverses all its votes"), then its votes,
/// flags, profile, summary, suspension and network observations are deleted, and the ledger is
/// anonymised — the entries stay (other users' reputation is made of them) but name
/// <see cref="ModerationIds.DeletedUser"/> instead of the account.
/// </summary>
/// <remarks>Idempotent, as <see cref="ISparkAccountDeletionHandler{TUser}"/> requires: a retry repeats nothing that already happened.</remarks>
internal sealed partial class ModerationAccountDeletionHandler<TUser> : ISparkAccountDeletionHandler<TUser>
    where TUser : SparkUser
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ReputationLedger ledger;
    [Inject] private readonly ModerationAudit audit;

    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return;
        await ModerationAccountEraser.EraseAsync(documentStore, ledger, userId, cancellationToken);
        await audit.WriteAsync("account-deleted", null, null, ModerationIds.DeletedUser, cancellationToken: cancellationToken, actorId: "system");
    }
}

internal static class ModerationAccountEraser
{
    public static async Task EraseAsync(IDocumentStore store, ReputationLedger ledger, string userId, CancellationToken cancellationToken)
    {
        var cast = await SparkModeration.VotesCastByAsync(store, userId, cancellationToken);
        await ledger.ReverseVotesAsync(cast, "account-deleted", null, cancellationToken);

        await DeleteWhereAsync<ModerationVote>(store, v => v.VoterId == userId, cancellationToken);
        await DeleteWhereAsync<ModerationFlag>(store, f => f.FlaggerId == userId, cancellationToken);
        await DeleteWhereAsync<ModerationIpObservation>(store, o => o.UserId == userId, cancellationToken);

        using (var session = store.OpenAsyncSession())
        {
            session.Delete(ModerationIds.Profile(userId));
            session.Delete(ModerationIds.Summary(userId));
            session.Delete(ModerationIds.Suspension(userId));
            await session.SaveChangesAsync(cancellationToken);
        }

        // Anonymise the ledger in pages: as recipient and as voter.
        for (var page = 0; page < 1000; page++)
        {
            using var session = store.OpenAsyncSession();
            var entries = await session.Query<ReputationEvent>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(e => e.UserId == userId || e.VoterId == userId)
                .Take(512)
                .ToListAsync(cancellationToken);
            if (entries.Count == 0)
                break;
            foreach (var entry in entries)
            {
                if (entry.UserId == userId) entry.UserId = ModerationIds.DeletedUser;
                if (entry.VoterId == userId) entry.VoterId = ModerationIds.DeletedUser;
            }
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task DeleteWhereAsync<T>(IDocumentStore store, System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken cancellationToken)
    {
        for (var page = 0; page < 1000; page++)
        {
            using var session = store.OpenAsyncSession();
            var ids = await session.Query<T>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(predicate)
                .Take(512)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
                break;
            foreach (var item in ids)
                session.Delete(item!);
            await session.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>The account's moderation data in the personal-data export (D8).</summary>
internal sealed partial class ModerationPersonalDataContributor<TUser> : ISparkPersonalDataContributor<TUser>
    where TUser : SparkUser
{
    [Inject] private readonly IDocumentStore documentStore;

    public string Name => "moderation";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return null;
        using var session = documentStore.OpenAsyncSession();
        var votes = await session.Query<ModerationVote>().Where(v => v.VoterId == userId).Take(10_000).ToListAsync(cancellationToken);
        var flags = await session.Query<ModerationFlag>().Where(f => f.FlaggerId == userId).Take(10_000).ToListAsync(cancellationToken);
        var summary = await session.LoadAsync<ReputationSummary>(ModerationIds.Summary(userId), cancellationToken);
        var suspension = await session.LoadAsync<ModerationSuspension>(ModerationIds.Suspension(userId), cancellationToken);
        return new
        {
            reputation = summary?.Total ?? 0,
            votes = votes.Where(v => v.Direction != 0).Select(v => new { v.TargetId, v.TargetType, v.Direction, v.CastAtUtc }),
            flags = flags.Select(f => new { f.TargetId, f.TargetType, f.Reason, f.RaisedAtUtc, f.Status }),
            suspension = suspension is null ? null : new { suspension.UntilUtc, suspension.Reason, suspension.SuspendedAtUtc },
        };
    }
}
