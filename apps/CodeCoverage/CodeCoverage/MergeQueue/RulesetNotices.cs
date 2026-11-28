using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// Writes down what a repository's managers should hear about its merge-queue rulesets (#714 Dec5, Dec11), for the bell to
/// deliver at sign-in. Every method writes in the caller's session and never saves: the queue saves the notice with the
/// step that found it.
/// </summary>
public interface IRulesetNotices
{
    /// <summary>
    /// A queued pull request waited, past one poll, for <paramref name="checks"/>, which a rule requires and which never
    /// reported, while everything else had finished (Dec5). <paramref name="reportedOnHead"/> are the names that did
    /// report there, which the setup offers to pick the summary job from (Dec6).
    /// </summary>
    Task RaiseMissingCheckAsync(Repository repository, string baseBranch, IReadOnlyCollection<string> checks, IReadOnlyCollection<string> reportedOnHead,
        int pullRequest, CancellationToken cancellationToken = default);

    /// <summary>GitHub refused a merge, and each name in <paramref name="reportingApps"/> is pinned to an App other than the one that reports it (Dec4, Dec5).</summary>
    Task RaisePinMismatchAsync(Repository repository, string baseBranch, IReadOnlyDictionary<string, long> reportingApps, int pullRequest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The rules just read for <paramref name="baseBranch"/> no longer require what an open notice of that branch is about
    /// (someone fixed them, by the setup or by hand): the notice is resolved, and nobody else is told.
    /// </summary>
    Task ResolveFixedAsync(Repository repository, string baseBranch, ForgeBranchRules rules, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rules of <paramref name="baseBranch"/> require one check per CI job (<see cref="MergeQueueRulesetPlanner.RequiresOneCheckPerJob"/>):
    /// the managers are offered the switch to one summary job, once per repository (Dec11). A notice that exists already,
    /// open or resolved, is left alone; one whose branch now requires a single job is resolved.
    /// </summary>
    Task OfferSummaryJobAsync(Repository repository, string baseBranch, ForgeBranchRules rules, CancellationToken cancellationToken = default);

    /// <summary>"Set up rulesets" applied its plan: every open notice of the repository is resolved.</summary>
    Task ResolveAllAsync(string repositoryId, CancellationToken cancellationToken = default);

    /// <summary>The repository's open notices, which "Set up rulesets" starts its form from (Dec6).</summary>
    Task<IReadOnlyList<RulesetNotice>> ListOpenAsync(string repositoryId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRulesetNotices"/>
[Register(typeof(IRulesetNotices), ServiceLifetime.Scoped)]
public partial class RulesetNotices : IRulesetNotices
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly TimeProvider time;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public Task RaiseMissingCheckAsync(Repository repository, string baseBranch, IReadOnlyCollection<string> checks, IReadOnlyCollection<string> reportedOnHead,
        int pullRequest, CancellationToken cancellationToken = default)
        => RaiseAsync(repository, ERulesetNoticeKind.MissingCheck, baseBranch, checks, notice =>
        {
            notice.ReportedOnHead = [.. reportedOnHead.Where(name => !checks.Contains(name, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            notice.ReportingApps = [];
            notice.PullRequest = pullRequest;
        }, cancellationToken);

    public Task RaisePinMismatchAsync(Repository repository, string baseBranch, IReadOnlyDictionary<string, long> reportingApps, int pullRequest,
        CancellationToken cancellationToken = default)
        => RaiseAsync(repository, ERulesetNoticeKind.PinMismatch, baseBranch, [.. reportingApps.Keys], notice =>
        {
            notice.ReportingApps = new Dictionary<string, long>(reportingApps, StringComparer.Ordinal);
            notice.ReportedOnHead = [];
            notice.PullRequest = pullRequest;
        }, cancellationToken);

    public async Task ResolveFixedAsync(Repository repository, string baseBranch, ForgeBranchRules rules, CancellationToken cancellationToken = default)
    {
        var id = repository.Id.Value;
        if (await OpenAsync(id, ERulesetNoticeKind.MissingCheck, cancellationToken) is { } missing
            && missing.BaseBranch == baseBranch
            && !missing.Checks.Any(name => rules.RequiredChecks.Contains(name, StringComparer.Ordinal)))
            missing.ResolvedAtUtc = Now;

        if (await OpenAsync(id, ERulesetNoticeKind.PinMismatch, cancellationToken) is { } pins && pins.BaseBranch == baseBranch)
        {
            var sources = rules.RequiredCheckSources();
            // Fixed once no rule pins one of the names to an App other than the one that reports it.
            if (!pins.ReportingApps.Any(pair => sources.Any(source => source.Check.Context == pair.Key
                    && source.Check.IntegrationId is { } pinned && pinned != pair.Value)))
                pins.ResolvedAtUtc = Now;
        }
    }

    public async Task OfferSummaryJobAsync(Repository repository, string baseBranch, ForgeBranchRules rules, CancellationToken cancellationToken = default)
    {
        var id = RulesetNotice.DocumentId(repository.Id.Value, ERulesetNoticeKind.SummaryJob);
        var notice = await session.LoadAsync<RulesetNotice>(id, cancellationToken);
        if (!MergeQueueRulesetPlanner.RequiresOneCheckPerJob(rules.RequiredChecks))
        {
            if (notice is not null && notice.IsOpen() && notice.BaseBranch == baseBranch)
                notice.ResolvedAtUtc = Now;
            return;
        }
        // Once per repository: offered already (open, or resolved by the switch or the setup), it is not offered again.
        if (notice is not null) return;

        var perJob = rules.RequiredChecks.Where(name => !MergeQueueRulesetPlanner.AppCheckNames.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        await session.StoreAsync(new RulesetNotice
        {
            Repository = repository.Id.Value,
            RepositoryName = repository.FullName,
            Kind = ERulesetNoticeKind.SummaryJob,
            Key = Key(ERulesetNoticeKind.SummaryJob, baseBranch, []),
            BaseBranch = baseBranch,
            Checks = perJob,
            RaisedAtUtc = Now,
        }, id, cancellationToken);
    }

    public async Task ResolveAllAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        foreach (var notice in await ListOpenAsync(repositoryId, cancellationToken))
            notice.ResolvedAtUtc = Now;
    }

    public async Task<IReadOnlyList<RulesetNotice>> ListOpenAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        var loaded = await session.LoadAsync<RulesetNotice>(
            Enum.GetValues<ERulesetNoticeKind>().Select(kind => RulesetNotice.DocumentId(repositoryId, kind)), cancellationToken);
        return [.. loaded.Values.OfType<RulesetNotice>().Where(notice => notice.IsOpen())];
    }

    private async Task RaiseAsync(Repository repository, ERulesetNoticeKind kind, string baseBranch, IReadOnlyCollection<string> checks,
        Action<RulesetNotice> fill, CancellationToken cancellationToken)
    {
        var id = RulesetNotice.DocumentId(repository.Id.Value, kind);
        var names = checks.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var key = Key(kind, baseBranch, names);
        var notice = await session.LoadAsync<RulesetNotice>(id, cancellationToken);
        if (notice is null)
        {
            notice = new RulesetNotice
            {
                Repository = repository.Id.Value,
                Kind = kind,
            };
            await session.StoreAsync(notice, id, cancellationToken);
        }
        else if (notice.IsOpen() && notice.Key == key)
        {
            // The same drift seen again: whoever was told stays told.
            fill(notice);
            return;
        }

        notice.RepositoryName = repository.FullName;
        notice.Key = key;
        notice.BaseBranch = baseBranch;
        notice.Checks = names;
        notice.RaisedAtUtc = Now;
        notice.ResolvedAtUtc = null;
        notice.NotifiedUsers = [];
        fill(notice);
    }

    private async Task<RulesetNotice?> OpenAsync(string repositoryId, ERulesetNoticeKind kind, CancellationToken cancellationToken)
        => await session.LoadAsync<RulesetNotice>(RulesetNotice.DocumentId(repositoryId, kind), cancellationToken) is { } notice && notice.IsOpen()
            ? notice
            : null;

    private static string Key(ERulesetNoticeKind kind, string baseBranch, IReadOnlyList<string> names)
        => $"{kind}:{baseBranch}:{string.Join('\n', names)}";
}
