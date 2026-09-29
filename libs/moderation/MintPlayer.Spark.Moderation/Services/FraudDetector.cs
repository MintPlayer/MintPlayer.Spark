using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Moderation.Indexes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>What one detector run did.</summary>
public sealed class FraudDetectionReport
{
    public List<string> ReversedCases { get; } = [];
    public List<string> OpenedCases { get; } = [];
    public int ReversedVotes { get; set; }
}

/// <summary>
/// Fraud measure 6, the nightly detector over the last <c>DetectorWindowDays</c>:
/// <list type="bullet">
/// <item><b>serial</b> (≥ <c>SerialVotesIn24Hours</c> votes A→B inside 24 h) and <b>concentration</b>
/// (A cast ≥ <c>ConcentrationMinVotes</c>, ≥ <c>ConcentrationPercent</c>% of them on B) are
/// <b>reversed automatically</b> (compensating entries, measure 7) and recorded as decided cases;</item>
/// <item><b>reciprocal</b> voting, <b>fast voting</b> (&lt; <c>FastVoteSeconds</c> after the post) and a
/// <b>registration cluster</b> (accounts created within <c>RegistrationClusterMinutes</c> that share a
/// network hash or a non-webmail domain and vote for each other) only <b>open a review case</b>:
/// shared NATs and colleagues look exactly like this.</item>
/// </list>
/// </summary>
/// <remarks>
/// Idempotent: case ids are deterministic per (rule, accounts); an open case is refreshed, a decided
/// one is left alone, and a reversal writes nothing for an entry that already has its compensation.
/// </remarks>
internal sealed partial class FraudDetector
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ReputationLedger ledger;
    [Inject] private readonly IModerationAccounts accounts;
    [Inject] private readonly ModerationAudit audit;
    [Inject] private readonly IOptions<SparkModerationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly ILogger<FraudDetector> logger;

    public async Task<FraudDetectionReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var fraud = options.Value.Fraud;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var windowStart = now.AddDays(-fraud.DetectorWindowDays);
        var report = new FraudDetectionReport();

        var votes = await ActiveVotesSinceAsync(windowStart, cancellationToken);
        var pairs = await PairCountsSinceAsync(ModerationIds.DayNumber(windowStart), cancellationToken);

        // ---- serial: ≥ N votes A→B within any 24 h -------------------------------------------------
        foreach (var group in votes.Where(v => v.AuthorId is not null).GroupBy(v => (v.VoterId, AuthorId: v.AuthorId!)))
        {
            var ordered = group.OrderBy(v => v.CastAtUtc).ToList();
            var serial = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0, j = 0; j < ordered.Count; j++)
            {
                while (ordered[j].CastAtUtc - ordered[i].CastAtUtc > TimeSpan.FromHours(24))
                    i++;
                if (j - i + 1 >= fraud.SerialVotesIn24Hours)
                {
                    for (var k = i; k <= j; k++)
                        serial.Add(ordered[k].Id!);
                }
            }
            if (serial.Count > 0)
                await ReverseAsync("serial", group.Key.VoterId, group.Key.AuthorId, serial.ToList(),
                    $"{serial.Count} votes from one voter to one author within 24 hours.", report, now, cancellationToken);
        }

        // ---- concentration: ≥ P% of A's ≥ M votes on B -------------------------------------------
        foreach (var byVoter in pairs.GroupBy(p => p.Key.VoterId))
        {
            var total = byVoter.Sum(p => p.Value);
            if (total < fraud.ConcentrationMinVotes)
                continue;
            foreach (var pair in byVoter.Where(p => p.Value * 100 >= fraud.ConcentrationPercent * total))
            {
                var voteIds = votes.Where(v => v.VoterId == pair.Key.VoterId && v.AuthorId == pair.Key.AuthorId).Select(v => v.Id!).ToList();
                await ReverseAsync("concentration", pair.Key.VoterId, pair.Key.AuthorId, voteIds,
                    $"{pair.Value} of the voter's {total} votes went to one author.", report, now, cancellationToken);
            }
        }

        // ---- reciprocal: A→B and B→A, each ≥ R ---------------------------------------------------
        foreach (var pair in pairs.Where(p => string.CompareOrdinal(p.Key.VoterId, p.Key.AuthorId) < 0))
        {
            if (pair.Value < fraud.ReciprocalMinVotesEachWay)
                continue;
            if (pairs.GetValueOrDefault((pair.Key.AuthorId, pair.Key.VoterId)) is var back && back < fraud.ReciprocalMinVotesEachWay)
                continue;
            var voteIds = votes.Where(v => (v.VoterId == pair.Key.VoterId && v.AuthorId == pair.Key.AuthorId)
                                            || (v.VoterId == pair.Key.AuthorId && v.AuthorId == pair.Key.VoterId)).Select(v => v.Id!).ToList();
            await OpenCaseAsync("reciprocal", pair.Key.VoterId, pair.Key.AuthorId, voteIds,
                $"{pair.Value} votes one way and {back} the other.", report, now, cancellationToken);
        }

        // ---- fast voting: ≥ K votes cast < S seconds after the post ------------------------------
        foreach (var byVoter in votes.Where(v => v.TargetPostedAtUtc is not null && (v.CastAtUtc - v.TargetPostedAtUtc!.Value).TotalSeconds < fraud.FastVoteSeconds)
                     .GroupBy(v => v.VoterId))
        {
            var fast = byVoter.ToList();
            if (fast.Count < fraud.FastVoteMinCount)
                continue;
            var author = fast.GroupBy(v => v.AuthorId ?? string.Empty).OrderByDescending(g => g.Count()).First().Key;
            await OpenCaseAsync("fast-voting", byVoter.Key, author, fast.Select(v => v.Id!).ToList(),
                $"{fast.Count} votes cast less than {fraud.FastVoteSeconds} s after the post appeared.", report, now, cancellationToken);
        }

        // ---- registration cluster ------------------------------------------------------------------
        await DetectRegistrationClustersAsync(votes, report, now, cancellationToken);

        logger.LogInformation("Fraud detector: {Reversed} case(s) reversed ({Votes} votes), {Opened} case(s) opened.",
            report.ReversedCases.Count, report.ReversedVotes, report.OpenedCases.Count);
        return report;
    }

    private async Task DetectRegistrationClustersAsync(IReadOnlyList<ModerationVote> votes, FraudDetectionReport report, DateTime now, CancellationToken cancellationToken)
    {
        var fraud = options.Value.Fraud;
        var linked = votes.Where(v => v.AuthorId is not null && v.AuthorId != v.VoterId)
            .Select(v => string.CompareOrdinal(v.VoterId, v.AuthorId) < 0 ? (A: v.VoterId, B: v.AuthorId!) : (A: v.AuthorId!, B: v.VoterId))
            .Distinct()
            .ToList();
        if (linked.Count == 0)
            return;

        var ids = linked.SelectMany(p => new[] { p.A, p.B }).Distinct(StringComparer.Ordinal).ToList();
        var known = await accounts.GetAsync(ids, cancellationToken);
        var networks = await NetworksOfAsync(ids, cancellationToken);
        var webmail = new HashSet<string>(fraud.WebmailDomains, StringComparer.OrdinalIgnoreCase);

        foreach (var (a, b) in linked)
        {
            if (!known.TryGetValue(a, out var accountA) || !known.TryGetValue(b, out var accountB))
                continue;
            if (accountA.CreatedAtUtc is not { } createdA || accountB.CreatedAtUtc is not { } createdB)
                continue;
            if (Math.Abs((createdA - createdB).TotalMinutes) > fraud.RegistrationClusterMinutes)
                continue;

            var sharedNetwork = networks.GetValueOrDefault(a)?.Overlaps(networks.GetValueOrDefault(b) ?? []) == true;
            var sharedDomain = accountA.EmailDomain is { } domain && domain == accountB.EmailDomain && !webmail.Contains(domain);
            if (!sharedNetwork && !sharedDomain)
                continue;

            var voteIds = votes.Where(v => (v.VoterId == a && v.AuthorId == b) || (v.VoterId == b && v.AuthorId == a)).Select(v => v.Id!).ToList();
            var signal = sharedNetwork ? "share a network hash" : $"share the domain {accountA.EmailDomain}";
            await OpenCaseAsync("registration-cluster", a, b, voteIds,
                $"Created {Math.Abs((createdA - createdB).TotalMinutes):0} min apart, {signal}, and vote for each other.", report, now, cancellationToken);
        }
    }

    /// <summary>Each user's network observations as <c>{keyId}:{hash}</c> — equal only under the same key period.</summary>
    internal async Task<Dictionary<string, HashSet<string>>> NetworksOfAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var session = documentStore.OpenAsyncSession();
        foreach (var chunk in userIds.Chunk(256))
        {
            var rows = await ModerationQueries.StreamAllAsync(session, () => session.Query<ModerationIpObservation>().Where(o => o.UserId.In(chunk)), cancellationToken);
            foreach (var (_, o) in rows)
            {
                if (!result.TryGetValue(o.UserId, out var set))
                    result[o.UserId] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(o.KeyId + ":" + o.Hash);
            }
        }
        return result;
    }

    private async Task ReverseAsync(string rule, string voterId, string authorId, IReadOnlyList<string> voteIds, string summary, FraudDetectionReport report, DateTime now, CancellationToken cancellationToken)
    {
        var caseId = ModerationIds.FraudCase(rule, voterId, authorId);
        using (var session = documentStore.OpenAsyncSession())
        {
            var existing = await session.LoadAsync<ReviewCase>(caseId, cancellationToken);
            var reviewCase = existing ?? new ReviewCase { Kind = rule, AccountIds = [voterId, authorId], OpenedAtUtc = now };
            reviewCase.VoteIds = reviewCase.VoteIds.Union(voteIds).ToList();
            reviewCase.Summary = summary;
            reviewCase.Status = ModerationCaseStatus.Reversed;
            reviewCase.Decision = "auto-reverse";
            reviewCase.DecidedAtUtc ??= now;
            reviewCase.DecidedBy = null;
            await session.StoreAsync(reviewCase, caseId, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        var changed = await ledger.ReverseVotesAsync(voteIds, rule, caseId, cancellationToken);
        if (changed.Count > 0)
        {
            report.ReversedVotes += voteIds.Count;
            await audit.WriteAsync("auto-reverse:" + rule, null, null, voterId, caseId, details: summary, cancellationToken: cancellationToken, actorId: "system");
        }
        if (!report.ReversedCases.Contains(caseId))
            report.ReversedCases.Add(caseId);
    }

    private async Task OpenCaseAsync(string rule, string a, string b, IReadOnlyList<string> voteIds, string summary, FraudDetectionReport report, DateTime now, CancellationToken cancellationToken)
    {
        var caseId = ModerationIds.FraudCase(rule, a, b);
        using var session = documentStore.OpenAsyncSession();
        var reviewCase = await session.LoadAsync<ReviewCase>(caseId, cancellationToken);
        if (reviewCase is not null && reviewCase.Status != ModerationCaseStatus.Open)
            return; // Decided by a human: the detector does not reopen it.

        reviewCase ??= new ReviewCase { Kind = rule, AccountIds = [a, b], OpenedAtUtc = now };
        reviewCase.VoteIds = reviewCase.VoteIds.Union(voteIds).ToList();
        reviewCase.Summary = summary;
        await session.StoreAsync(reviewCase, caseId, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        if (!report.OpenedCases.Contains(caseId))
            report.OpenedCases.Add(caseId);
    }

    private async Task<IReadOnlyList<ModerationVote>> ActiveVotesSinceAsync(DateTime since, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var rows = await ModerationQueries.StreamAllAsync(session, () => session.Query<ModerationVote>().Where(v => v.CastAtUtc >= since && v.Direction != 0), cancellationToken);
        foreach (var (id, vote) in rows)
            vote.Id = id;
        return rows.Select(r => r.Document).ToList();
    }

    /// <summary>The voter→recipient matrix from <see cref="Moderation_VotePairs"/>, summed over the window's days.</summary>
    internal async Task<Dictionary<(string VoterId, string AuthorId), int>> PairCountsSinceAsync(int sinceDayNumber, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var result = new Dictionary<(string, string), int>();
        var rows = await ModerationQueries.StreamAllAsync(session,
            () => session.Query<Moderation_VotePairs.Result, Moderation_VotePairs>().Where(r => r.DayNumber >= sinceDayNumber), cancellationToken);
        foreach (var (_, row) in rows)
        {
            var key = (row.VoterId, row.AuthorId);
            result[key] = result.GetValueOrDefault(key) + row.Count;
        }
        return result;
    }
}
