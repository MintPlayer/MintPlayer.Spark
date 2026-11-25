using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Moderation.Indexes;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>A case as the review queue lists it.</summary>
public sealed class ModerationCaseSummary
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Status { get; init; }
    public string? TargetId { get; init; }
    public string? TargetType { get; init; }
    public IReadOnlyList<string> AccountIds { get; init; } = [];
    public int FlagCount { get; init; }
    public int VoteCount { get; init; }
    public string? Summary { get; init; }
    public DateTime OpenedAtUtc { get; init; }
    public string? Decision { get; init; }
}

/// <summary>
/// The review surface of one case (fraud measure 8): the voter→target matrix between its accounts,
/// the timeline of its votes against the posts' creation, the accounts' ages side by side, how many
/// accounts each shares a network hash with (never an address), reputation before and after
/// reversing the case's votes, and the rule that fired.
/// </summary>
public sealed class ModerationCaseDetail
{
    public required ModerationCaseSummary Case { get; init; }
    public IReadOnlyList<ModerationPairCell> Matrix { get; init; } = [];
    public IReadOnlyList<ModerationTimelineEntry> Timeline { get; init; } = [];
    public IReadOnlyList<ModerationCaseAccount> Accounts { get; init; } = [];
    public IReadOnlyList<ModerationFlagView> Flags { get; init; } = [];
}

public sealed record ModerationPairCell(string VoterId, string AuthorId, int Votes);

public sealed record ModerationTimelineEntry(string VoteId, string VoterId, string? AuthorId, string TargetId, int Direction, DateTime CastAtUtc, DateTime? TargetPostedAtUtc, double? SecondsAfterPost);

public sealed record ModerationCaseAccount(string Id, DateTime? CreatedAtUtc, int? AgeDays, string? RegistrationMethod, int SharesNetworkWith, int ReputationBefore, int ReputationAfter);

public sealed record ModerationFlagView(string FlaggerId, string Reason, DateTime RaisedAtUtc, string Status);

/// <summary>One line of a user's own reputation history. Never names a voter.</summary>
public sealed record ModerationReputationLine(string Label, string Kind, int Points, string? TargetId, string? TargetType, DateTime AtUtc, bool Pending);

/// <summary>The read side: the review queue, a case's evidence, the audit log and a user's own history.</summary>
internal sealed partial class ModerationReview
{
    [Inject] private readonly ISparkStore store;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkCurrentPrincipal currentUser;
    [Inject] private readonly IModerationAccounts accounts;
    [Inject] private readonly FraudDetector detector;
    [Inject] private readonly TimeProvider timeProvider;

    public async Task<IReadOnlyList<ModerationCaseSummary>> ListCasesAsync(string? status, int skip, int take, CancellationToken cancellationToken)
    {
        await permissions.EnsureAuthorizedAsync(ModerationRights.Review, ModerationRights.Target, cancellationToken);
        using var session = store.OpenSession();
        var query = session.Query<Moderation_Cases.Result, Moderation_Cases>();
        var wanted = string.IsNullOrEmpty(status) ? ModerationCaseStatus.Open : status;
        if (wanted != "all")
            query = query.Where(c => c.Status == wanted);
        var ids = await session.ToListAsync(query.OrderByDescending(c => c.OpenedAtUtc).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).Select(c => c.Id), cancellationToken);
        return (await LoadInOrderAsync<ReviewCase>(session, ids, cancellationToken)).Select(Summarize).ToList();
    }

    /// <summary>The documents <paramref name="ids"/> names, in that order (a view's sort); one batch, ids deleted meanwhile skipped.</summary>
    private static async Task<List<T>> LoadInOrderAsync<T>(ISparkSession session, IReadOnlyList<string?> ids, CancellationToken cancellationToken) where T : class
    {
        var wanted = ids.OfType<string>().ToList();
        var loaded = await session.LoadAsync<T>(wanted, cancellationToken);
        return [.. wanted.Select(id => loaded.GetValueOrDefault(id)).OfType<T>()];
    }

    public async Task<ModerationCaseDetail> GetCaseAsync(string caseId, CancellationToken cancellationToken)
    {
        await permissions.EnsureAuthorizedAsync(ModerationRights.Review, ModerationRights.Target, cancellationToken);
        if (string.IsNullOrEmpty(caseId) || !caseId.StartsWith(ModerationIds.CasePrefix, StringComparison.OrdinalIgnoreCase))
            throw new SparkRowLevelAccessDeniedException("Review/Moderation");

        using var session = store.OpenSession();
        session.MaxNumberOfRequests = int.MaxValue;
        var reviewCase = await session.LoadAsync<ReviewCase>(caseId, cancellationToken)
                         ?? throw new SparkRowLevelAccessDeniedException("Review/Moderation");

        var votes = (await session.LoadAsync<ModerationVote>(reviewCase.VoteIds, cancellationToken)).Values.Where(v => v is not null).Select(v => v!).ToList();
        var timeline = votes.OrderBy(v => v.CastAtUtc).Select(v => new ModerationTimelineEntry(
            v.Id.Value, v.VoterId, v.AuthorId, v.TargetId, v.Direction, v.CastAtUtc, v.TargetPostedAtUtc,
            v.TargetPostedAtUtc is { } posted ? (v.CastAtUtc - posted).TotalSeconds : null)).ToList();

        var accountIds = reviewCase.AccountIds.Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var matrix = new List<ModerationPairCell>();
        if (accountIds.Count > 1)
        {
            var window = timeProvider.GetUtcNow().UtcDateTime.AddDays(-90);
            var pairs = await detector.PairCountsSinceAsync(ModerationIds.DayNumber(window), cancellationToken);
            foreach (var voter in accountIds)
            foreach (var author in accountIds.Where(a => a != voter))
                matrix.Add(new ModerationPairCell(voter, author, pairs.GetValueOrDefault((voter, author))));
        }

        // Reputation before/after: the credited, not-yet-compensated points the case's votes gave.
        var eventIds = votes.SelectMany(v => v.EventIds).Distinct().ToArray();
        var events = eventIds.Length == 0 ? [] : (await session.LoadAsync<ReputationEvent>(eventIds, cancellationToken)).Values.Where(e => e is not null).Select(e => e!).ToList();
        var summaries = await session.LoadAsync<ReputationSummary>(accountIds.Select(id => ModerationIds.Summary(id).Value), cancellationToken);
        var known = await accounts.GetAsync(accountIds, cancellationToken);
        var networks = await detector.NetworksOfAsync(accountIds, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var caseAccounts = new List<ModerationCaseAccount>();
        foreach (var id in accountIds)
        {
            var before = summaries.GetValueOrDefault(ModerationIds.Summary(id).Value)?.Total ?? 0;
            var gained = events.Where(e => e.UserId == id && e.Credited && e.CompensatedById is null).Sum(e => e.Points);
            known.TryGetValue(id, out var account);
            caseAccounts.Add(new ModerationCaseAccount(
                id, account?.CreatedAtUtc, account is null ? null : account.AgeDays(now), account?.RegistrationMethod,
                await SharesNetworkWithAsync(id, networks.GetValueOrDefault(id), cancellationToken),
                before, before - gained));
        }

        var flags = reviewCase.Kind == "flag"
            ? (await LoadInOrderAsync<ModerationFlag>(session, await session.ToListAsync(session.Query<Moderation_Flags.Result, Moderation_Flags>()
                    .Where(f => f.CaseId == caseId).Select(f => f.Id).Take(200), cancellationToken), cancellationToken))
                .Select(f => new ModerationFlagView(f.FlaggerId, f.Reason, f.RaisedAtUtc, f.Status)).ToList()
            : [];

        return new ModerationCaseDetail
        {
            Case = Summarize(reviewCase),
            Matrix = matrix,
            Timeline = timeline,
            Accounts = caseAccounts,
            Flags = flags,
        };
    }

    /// <summary>How many other accounts share any network hash with <paramref name="userId"/> — a count, never an address.</summary>
    private async Task<int> SharesNetworkWithAsync(string userId, HashSet<string>? networks, CancellationToken cancellationToken)
    {
        if (networks is null || networks.Count == 0)
            return 0;
        using var session = store.OpenSession();
        List<string> hashes = [.. networks.Select(n => n[(n.IndexOf(':') + 1)..]).Distinct()];
        var others = await session.ToListAsync(session.Query<Moderation_IpObservations.Result, Moderation_IpObservations>()
            .Where(o => hashes.Contains(o.Hash) && o.UserId != userId)
            .Select(o => o.UserId)
            .Distinct()
            .Take(1000), cancellationToken);
        return others.Count;
    }

    public async Task<IReadOnlyList<ModerationAuditEntry>> ListAuditAsync(int skip, int take, CancellationToken cancellationToken)
    {
        await permissions.EnsureAuthorizedAsync(ModerationRights.Audit, ModerationRights.Target, cancellationToken);
        using var session = store.OpenSession();
        var ids = await session.ToListAsync(session.Query<Moderation_Audit.Result, Moderation_Audit>()
            .OrderByDescending(e => e.AtUtc)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .Select(e => e.Id), cancellationToken);
        return await LoadInOrderAsync<ModerationAuditEntry>(session, ids, cancellationToken);
    }

    /// <summary>
    /// The current user's own ledger, newest first. A reversal is shown as "Voting corrected (−N)",
    /// summed per case, without naming the voters (fraud measure 7).
    /// </summary>
    public async Task<IReadOnlyList<ModerationReputationLine>> MyHistoryAsync(int take, CancellationToken cancellationToken)
    {
        var userId = currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : throw new SparkAccessDeniedException("Moderation");
        using var session = store.OpenSession();
        var events = await LoadInOrderAsync<ReputationEvent>(session, await session.ToListAsync(session.Query<Moderation_Ledger.Result, Moderation_Ledger>()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .Select(e => e.Id), cancellationToken), cancellationToken);

        var lines = new List<ModerationReputationLine>();
        foreach (var group in events.GroupBy(e => e.Kind == ReputationEventKinds.Reversal ? "reversal:" + (e.CaseId ?? e.RuleId) : e.Id.Value))
        {
            var first = group.First();
            if (first.Kind == ReputationEventKinds.Reversal)
            {
                var points = group.Sum(e => e.Points);
                lines.Add(new ModerationReputationLine($"Voting corrected ({points:+0;-0;0})", first.Kind, points, null, null, first.CreatedAtUtc, group.Any(e => !e.Credited)));
                continue;
            }
            lines.Add(new ModerationReputationLine(Label(first), first.Kind, first.Points, first.TargetId, first.TargetType, first.CreatedAtUtc, !first.Credited));
        }
        return lines;
    }

    private static string Label(ReputationEvent e) => e.Kind switch
    {
        ReputationEventKinds.UpvoteReceived => e.ZeroedBy is null ? "Up-vote" : "Up-vote (not counted)",
        ReputationEventKinds.DownvoteReceived => "Down-vote",
        ReputationEventKinds.DownvoteCast => "You down-voted",
        ReputationEventKinds.FlagUpheld => "Flag upheld",
        ReputationEventKinds.FlagDeclined => "Flag declined",
        ReputationEventKinds.Retraction => "Vote withdrawn",
        _ => e.Kind,
    };

    private static ModerationCaseSummary Summarize(ReviewCase c) => new()
    {
        Id = c.Id.Value,
        Kind = c.Kind,
        Status = c.Status,
        TargetId = c.TargetId,
        TargetType = c.TargetType,
        AccountIds = c.AccountIds,
        FlagCount = c.FlagCount,
        VoteCount = c.VoteIds.Count,
        Summary = c.Summary,
        OpenedAtUtc = c.OpenedAtUtc,
        Decision = c.Decision,
    };
}
