using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Moderation.Indexes;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// The ledger's write rules outside the vote path: compensating reversals (fraud measure 7),
/// crediting (measure 5) and recomputing the summaries the privilege provider reads (measure 2).
/// </summary>
internal sealed partial class ReputationLedger
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkModerationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Writes the compensation of <paramref name="original"/> into <paramref name="session"/>, unless it
    /// already has one. Idempotent by id (<see cref="ModerationIds.Compensation"/>) and by the
    /// original's <see cref="ReputationEvent.CompensatedById"/>: an entry is compensated once, whether
    /// by a retraction or a reversal, however often a rule fires.
    /// </summary>
    /// <returns>The compensation written, or null when there already was one.</returns>
    public static async Task<ReputationEvent?> CompensateAsync(IAsyncDocumentSession session, ReputationEvent original, string kind, DateTime nowUtc, string? ruleId = null, string? caseId = null)
    {
        if (original.CompensatedById is not null || original.Kind is ReputationEventKinds.Reversal or ReputationEventKinds.Retraction)
            return null;

        var id = ModerationIds.Compensation(original.Id!);
        if (await session.Advanced.ExistsAsync(id))
            return null;

        // A reversal (detector, moderator, deletion, merge) of an entry that already counts is a
        // decision, not something farmed: credited at once, so the total drops on the recompute that
        // follows instead of at the next crediting run. Every other compensation, and a reversal of a
        // still-pending entry, waits for the entry it cancels.
        var creditNow = kind == ReputationEventKinds.Reversal && original.Credited;
        var compensation = new ReputationEvent
        {
            Id = id,
            UserId = original.UserId,
            Kind = kind,
            Points = -original.Points,
            VoteWeight = -original.VoteWeight,
            VoterId = original.VoterId,
            VoteId = original.VoteId,
            TargetId = original.TargetId,
            TargetType = original.TargetType,
            CreatedAtUtc = nowUtc,
            Day = original.Day,
            // Never credited before the entry it cancels: a pending up-vote withdrawn inside the delay
            // nets to zero at crediting time instead of dipping below zero first.
            CreditableAfterUtc = original.CreditableAfterUtc > nowUtc ? original.CreditableAfterUtc : nowUtc,
            Credited = creditNow,
            CompensatesId = original.Id,
            RuleId = ruleId,
            CaseId = caseId,
        };
        await session.StoreAsync(compensation, string.Empty, id);
        original.CompensatedById = id;
        return compensation;
    }

    /// <summary>
    /// Reverses <paramref name="voteIds"/>: every ledger entry of each vote gets a
    /// <see cref="ReputationEventKinds.Reversal"/> compensation carrying the rule and case, the vote
    /// stops counting (direction 0) and the tally drops it. Never deletes anything.
    /// </summary>
    /// <returns>The recipients whose reputation changed, and the points reversed per recipient.</returns>
    public async Task<IReadOnlyDictionary<string, int>> ReverseVotesAsync(IReadOnlyCollection<string> voteIds, string ruleId, string? caseId, CancellationToken cancellationToken = default)
    {
        var changed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var chunk in voteIds.Distinct(StringComparer.Ordinal).Chunk(64))
        {
            using var session = OpenSession();
            var votes = await session.LoadAsync<ModerationVote>(chunk, cancellationToken);
            var eventIds = votes.Values.Where(v => v is not null).SelectMany(v => v!.EventIds).Distinct().ToArray();
            var events = eventIds.Length == 0 ? new Dictionary<string, ReputationEvent>() : await session.LoadAsync<ReputationEvent>(eventIds, cancellationToken);
            var now = UtcNow;

            foreach (var vote in votes.Values)
            {
                if (vote is null)
                    continue;
                foreach (var eventId in vote.EventIds)
                {
                    if (!events.TryGetValue(eventId, out var original) || original is null)
                        continue;
                    var compensation = await CompensateAsync(session, original, ReputationEventKinds.Reversal, now, ruleId, caseId);
                    if (compensation is not null && compensation.Points != 0)
                        changed[compensation.UserId] = changed.GetValueOrDefault(compensation.UserId) + compensation.Points;
                    else if (compensation is not null)
                        changed.TryAdd(compensation.UserId, 0);
                }

                if (vote.Direction != 0)
                {
                    var tally = await session.LoadAsync<ModerationTally>(ModerationIds.Tally(vote.TargetId), cancellationToken);
                    if (tally is not null)
                    {
                        if (vote.Direction > 0) tally.Up = Math.Max(0, tally.Up - 1);
                        else tally.Down = Math.Max(0, tally.Down - 1);
                    }
                }
                vote.Direction = 0;
                vote.EventIds = [];
            }

            await session.SaveChangesAsync(cancellationToken);
        }

        await RecomputeAsync(changed.Keys, cancellationToken);
        return changed;
    }

    /// <summary>
    /// Credits every entry whose delay has passed (fraud measure 5) and recomputes the recipients'
    /// summaries. The crediting job's body; bounded to <paramref name="maxPages"/> pages per call.
    /// </summary>
    /// <returns>How many entries were credited.</returns>
    public async Task<int> CreditDueAsync(int maxPages = 100, CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var users = new HashSet<string>(StringComparer.Ordinal);
        var credited = 0;

        for (var page = 0; page < maxPages; page++)
        {
            using var session = OpenSession();
            var due = await session.Query<Moderation_EntriesToCredit.Result, Moderation_EntriesToCredit>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(e => e.CreditableAfterUtc <= now)
                .OfType<ReputationEvent>()
                .Take(512)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
                break;

            foreach (var entry in due)
            {
                entry.Credited = true;
                users.Add(entry.UserId);
            }
            credited += due.Count;
            await session.SaveChangesAsync(cancellationToken);
        }

        await RecomputeAsync(users, cancellationToken);
        return credited;
    }

    /// <summary>Recomputes the stored <see cref="ReputationSummary"/> of each user from the ledger indexes.</summary>
    public async Task RecomputeAsync(IEnumerable<string> userIds, CancellationToken cancellationToken = default)
    {
        foreach (var userId in userIds.Where(u => !string.IsNullOrEmpty(u) && u != ModerationIds.DeletedUser).Distinct(StringComparer.Ordinal))
        {
            // Derived data: the last computation wins, no concurrency check.
            using var session = documentStore.OpenAsyncSession();
            var summary = await ComputeAsync(session, userId, cancellationToken);
            await session.StoreAsync(summary, ModerationIds.Summary(userId), cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The not-yet-credited points of <paramref name="userId"/>, read from the pending index at the
    /// moment of asking. The stored summary's <see cref="ReputationSummary.Pending"/> is only as fresh
    /// as the last recompute, and a vote does not recompute (that would cost the vote path two index
    /// waits); the index is updated by the vote's own transaction, so this read cannot miss a vote
    /// through a skipped or crashed recompute. Waits briefly for the index; when it is still stale the
    /// stale figure is returned rather than failing a badge.
    /// </summary>
    public async Task<int> ReadPendingAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        try
        {
            var row = await session.Query<Moderation_PendingReputation.Result, Moderation_PendingReputation>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.UserId == userId)
                .FirstOrDefaultAsync(cancellationToken);
            return row?.Points ?? 0;
        }
        catch (TimeoutException)
        {
            var row = await session.Query<Moderation_PendingReputation.Result, Moderation_PendingReputation>()
                .Where(c => c.UserId == userId)
                .FirstOrDefaultAsync(cancellationToken);
            return row?.Points ?? 0;
        }
    }

    /// <summary>
    /// The summary of <paramref name="userId"/> from the (non-stale) indexes. The diversity rule
    /// (fraud measure 2): vote-derived reputation counts toward privileges only when it came from at
    /// least <c>max(DiversityMinVoters, votes / DiversityVotersDivisor)</c> distinct voters over at
    /// least <c>votes / DiversityDaysDivisor</c> distinct days; otherwise only the non-vote part does.
    /// </summary>
    public async Task<ReputationSummary> ComputeAsync(IAsyncDocumentSession session, string userId, CancellationToken cancellationToken = default)
    {
        var fraud = options.Value.Fraud;
        var cells = await session.Query<Moderation_ReputationCells.Result, Moderation_ReputationCells>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(c => c.UserId == userId)
            .Take(int.MaxValue)
            .ToListAsync(cancellationToken);
        var pending = await session.Query<Moderation_PendingReputation.Result, Moderation_PendingReputation>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(c => c.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);

        var total = cells.Sum(c => c.Points);
        var voteCells = cells.Where(c => c.VoterId.Length > 0).ToList();
        var votePoints = voteCells.Sum(c => c.Points);
        var counted = voteCells.Where(c => c.Votes > 0).ToList();
        var votes = counted.Sum(c => c.Votes);
        var voters = counted.Select(c => c.VoterId).Distinct(StringComparer.Ordinal).Count();
        var days = counted.Select(c => c.Day).Distinct(StringComparer.Ordinal).Count();
        var diversity = votes == 0
            || (voters >= Math.Max(fraud.DiversityMinVoters, votes / fraud.DiversityVotersDivisor)
                && days >= votes / fraud.DiversityDaysDivisor);

        return new ReputationSummary
        {
            UserId = userId,
            Total = total,
            Pending = pending?.Points ?? 0,
            PrivilegeReputation = diversity ? total : total - votePoints,
            CreditedVotes = votes,
            DistinctVoters = voters,
            DistinctDays = days,
            DiversityMet = diversity,
            ComputedAtUtc = UtcNow,
        };
    }

    private IAsyncDocumentSession OpenSession()
    {
        var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
        return session;
    }
}
