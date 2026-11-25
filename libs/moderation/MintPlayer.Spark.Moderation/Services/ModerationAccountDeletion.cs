using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Moderation.Indexes;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// GDPR (D8): what Moderation holds about an account when it is deleted. Every vote the account cast
/// is reversed first (fraud measure 7: "deleting an account reverses all its votes"), then its votes,
/// flags, profile, summary, suspension and network observations are deleted, and the ledger is
/// anonymised — the entries stay (other users' reputation is made of them) but name
/// <see cref="ModerationIds.DeletedUser"/> instead of the account.
/// </summary>
/// <remarks>Idempotent, as <see cref="ISparkAccountDeletionHandler"/> requires: a retry repeats nothing that already happened.</remarks>
internal sealed partial class ModerationAccountDeletionHandler : ISparkAccountDeletionHandler
{
    [Inject] private readonly ISparkStore store;
    [Inject] private readonly ReputationLedger ledger;
    [Inject] private readonly ModerationAudit audit;

    public async Task OnDeletingAccountAsync(ISparkAccount user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return;
        await ModerationAccountEraser.EraseAsync(store, ledger, userId, cancellationToken);
        await audit.WriteAsync("account-deleted", null, null, ModerationIds.DeletedUser, cancellationToken: cancellationToken, actorId: "system");
    }
}

internal static class ModerationAccountEraser
{
    public static async Task EraseAsync(ISparkStore store, ReputationLedger ledger, string userId, CancellationToken cancellationToken)
    {
        var cast = await SparkModeration.VotesCastByAsync(store, userId, cancellationToken);
        await ledger.ReverseVotesAsync(cast, "account-deleted", null, cancellationToken);

        // By id, read from the views: a set-based delete over the collection would make RavenDB build an auto index (D42).
        await DeleteAllAsync<ModerationVote>(store, s => s.Query<Moderation_Votes.Result, Moderation_Votes>(ReputationLedger.IndexWait)
            .Where(v => v.VoterId == userId).Select(v => v.Id), cancellationToken);
        await DeleteAllAsync<ModerationFlag>(store, s => s.Query<Moderation_Flags.Result, Moderation_Flags>(ReputationLedger.IndexWait)
            .Where(f => f.FlaggerId == userId).Select(f => f.Id), cancellationToken);
        await DeleteAllAsync<ModerationIpObservation>(store, s => s.Query<Moderation_IpObservations.Result, Moderation_IpObservations>(ReputationLedger.IndexWait)
            .Where(o => o.UserId == userId).Select(o => o.Id), cancellationToken);

        using (var session = store.OpenSession())
        {
            session.Delete(ModerationIds.Profile(userId));
            session.Delete(ModerationIds.Summary(userId));
            session.Delete(ModerationIds.Suspension(userId));
            await session.SaveChangesAsync(cancellationToken);
        }

        // Anonymise the ledger in pages: as recipient and as voter.
        for (var page = 0; page < 1000; page++)
        {
            using var session = store.OpenSession();
            var ids = await session.ToListAsync(session.Query<Moderation_Ledger.Result, Moderation_Ledger>(ReputationLedger.IndexWait)
                .Where(e => e.UserId == userId || e.VoterId == userId)
                .Select(e => e.Id)
                .Take(512), cancellationToken);
            if (ids.Count == 0)
                break;
            var entries = await session.LoadAsync<ReputationEvent>(ids.OfType<string>(), cancellationToken);
            foreach (var entry in entries.Values)
            {
                if (entry is null)
                    continue;
                if (entry.UserId == userId) entry.UserId = ModerationIds.DeletedUser;
                if (entry.VoterId == userId) entry.VoterId = ModerationIds.DeletedUser;
            }
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task DeleteAllAsync<T>(ISparkStore store, Func<ISparkSession, IQueryable<string?>> ids, CancellationToken cancellationToken) where T : class
    {
        for (var page = 0; page < 1000; page++)
        {
            using var session = store.OpenSession();
            var found = await session.ToListAsync(ids(session).Take(512), cancellationToken);
            if (found.Count == 0)
                break;
            foreach (var id in found.OfType<string>())
                session.Delete(typeof(T), id);
            await session.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>The account's moderation data in the personal-data export (D8).</summary>
internal sealed partial class ModerationPersonalDataContributor : ISparkPersonalDataContributor
{
    [Inject] private readonly ISparkStore store;

    public string Name => "moderation";

    public async Task<object?> GetPersonalDataAsync(ISparkAccount user, CancellationToken cancellationToken)
    {
        if (user.Id is not { Length: > 0 } userId)
            return null;
        using var session = store.OpenSession();
        session.MaxNumberOfRequests = int.MaxValue;
        var voteIds = await session.ToListAsync(session.Query<Moderation_Votes.Result, Moderation_Votes>()
            .Where(v => v.VoterId == userId).Select(v => v.Id).Take(10_000), cancellationToken);
        var flagIds = await session.ToListAsync(session.Query<Moderation_Flags.Result, Moderation_Flags>()
            .Where(f => f.FlaggerId == userId).Select(f => f.Id).Take(10_000), cancellationToken);
        var votes = await session.LoadAsync<ModerationVote>(voteIds.OfType<string>(), cancellationToken);
        var flags = await session.LoadAsync<ModerationFlag>(flagIds.OfType<string>(), cancellationToken);
        var summary = await session.LoadAsync<ReputationSummary>(ModerationIds.Summary(userId).Value, cancellationToken);
        var suspension = await session.LoadAsync<ModerationSuspension>(ModerationIds.Suspension(userId).Value, cancellationToken);
        return new
        {
            reputation = summary?.Total ?? 0,
            votes = votes.Values.OfType<ModerationVote>().Where(v => v.Direction != 0).Select(v => new { v.TargetId, v.TargetType, v.Direction, v.CastAtUtc }),
            flags = flags.Values.OfType<ModerationFlag>().Select(f => new { f.TargetId, f.TargetType, f.Reason, f.RaisedAtUtc, f.Status }),
            suspension = suspension is null ? null : new { suspension.UntilUtc, suspension.Reason, suspension.SuspendedAtUtc },
        };
    }
}
