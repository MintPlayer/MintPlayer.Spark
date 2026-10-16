using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.Dependencies.Lifecycle;

// The account's "Dependency updates" fleet view and the repository panel (dependency-updates PRD §8, §13.8 N9, D-N5).
// Pure: the endpoint decides which repositories the viewer may see (#453) and passes only those, so nothing here can name a
// repository it was not given — the red versions of the fleet state included, which are filtered to the given ones.

/// <summary><c>GET api/browse/accounts/{provider}/{login}/dependency-updates</c>.</summary>
/// <param name="Enabled">Dependency updates are on for the account (the repositories may still override).</param>
/// <param name="PackagesTruncated">More packages qualified than <see cref="DependencyFleetView.MaxPackages"/>.</param>
public sealed record DependencyFleetResponse(bool Enabled, IReadOnlyList<DependencyRepoStatus> Repositories, IReadOnlyList<DependencyPackageRow> Packages,
    bool PackagesTruncated, IReadOnlyList<DependencyReadiness> Readiness);

/// <summary><c>GET api/browse/repos/{provider}/{owner}/{name}/dependency-updates</c>.</summary>
public sealed record DependencyRepoView(DependencyRepoStatus Status, IReadOnlyList<DependencyPlannedGroupView> Planned, IReadOnlyList<DependencyRunNote> History);

/// <param name="Health">One word for the chip: <c>off</c>, <c>onboarding</c>, <c>blocked</c>, <c>running</c>, <c>waiting</c>,
/// <c>bisecting</c>, <c>red</c>, <c>frozen</c>, <c>silent</c>, <c>failed</c>, <c>ok</c>, <c>new</c>.</param>
public sealed record DependencyRepoStatus(
    string Id, string FullName, string Name, bool IsPrivate, bool Enabled, string Health, string State, string? Outcome, string? OutcomeMessage,
    DateTime? LastRunAtUtc, DateTime? LastFinishedAtUtc, DateTimeOffset? NextSlotUtc, string? Schedule, DependencyPullRequestLink? PullRequest,
    string? Onboarding, string? OnboardingNote, IReadOnlyList<DependencyHoldView> Holds, IReadOnlyList<DependencyAwaitingView> AwaitingApproval,
    int PlannedGroups, IReadOnlyList<string> Pins, string? CoverageNote)
{
    /// <summary>The onboarding pull request (R16), open, merged or closed; null when none was opened.</summary>
    public DependencyPullRequestLink? OnboardingPullRequest { get; init; }

    /// <summary>R26: <c>coverage.yml</c>'s merge queue as last read: <c>enabled</c>, <c>disabled</c>, <c>absent</c>; null until read.</summary>
    public string? MergeQueueInFile { get; init; }

    /// <summary>R30: "waiting for the forge's rate limit until hh:mm (park n of m)"; null when nothing waits.</summary>
    public DependencyRateLimitView? RateLimit { get; init; }
}

/// <param name="UntilUtc">When the bus retries the waiting message.</param>
/// <param name="Park">This message's park count, 1-based.</param>
/// <param name="Budget">The parks it may take before it is dead-lettered (<c>MaxAttempts × RateLimitParksPerAttempt</c>).</param>
public sealed record DependencyRateLimitView(DateTime UntilUtc, int Park, int Budget);

public sealed record DependencyPullRequestLink(int Number, string? Url, bool Draft);

public sealed record DependencyHoldView(string Kind, string? Group, IReadOnlyList<string> DepNames, string Reason, string? By, DateTime CreatedAtUtc);

/// <param name="Command">What a writer comments on the pull request to approve it.</param>
public sealed record DependencyAwaitingView(string Group, string DepName, string? Current, string? Target, string Command);

public sealed record DependencyPlannedGroupView(string Group, string Level, bool AutoMerge, IReadOnlyList<DependencyPlannedItemView> Items);

public sealed record DependencyPlannedItemView(string DepName, string PackageFile, string From, string To);

public sealed record DependencyRunNote(DateTime AtUtc, string Text);

/// <summary>One package as the fleet uses it: which repository is on which version, and what the bot does with it there.</summary>
public sealed record DependencyPackageRow(string Ecosystem, string Name, IReadOnlyList<DependencyPackageUse> Uses);

/// <param name="Status">Null when nothing is going on for it in that repository; else what is (<c>in the pull request → 2.0.0</c>,
/// <c>awaiting approval → 3.0.0</c>, <c>held: …</c>, <c>red: 2.0.0</c>).</param>
public sealed record DependencyPackageUse(string RepositoryId, string FullName, string? Version, string Path, bool Dev, string? Status);

/// <summary>"Angular 23: N of M ready, blockers …" (§8, D-N5).</summary>
public sealed record DependencyReadiness(string Platform, string Anchor, string NextMajor, int Ready, int Total,
    IReadOnlyList<DependencyReadinessRepo> Repositories, IReadOnlyList<DependencyBlockerCount> Blockers);

/// <param name="Status"><c>on-target</c>, <c>in-pull-request</c>, <c>ready</c>, <c>blocked</c>, <c>held</c> or <c>not-offered</c>.</param>
public sealed record DependencyReadinessRepo(string RepositoryId, string FullName, string? Current, string Status, string? Detail, IReadOnlyList<string> Blockers);

public sealed record DependencyBlockerCount(string Name, int Repositories);

/// <summary>What the view is built from, for the repositories the viewer may see.</summary>
public sealed record DependencyFleetInput
{
    public required IReadOnlyList<Repository> Repositories { get; init; }
    public Account? Account { get; init; }
    public IReadOnlyDictionary<string, DependencyUpdateRun> Runs { get; init; } = new Dictionary<string, DependencyUpdateRun>();
    public IReadOnlyDictionary<string, DependencyUpdateState> States { get; init; } = new Dictionary<string, DependencyUpdateState>();
    public IReadOnlyDictionary<string, DependencyRunReport> Reports { get; init; } = new Dictionary<string, DependencyRunReport>();
    public IReadOnlyDictionary<string, RepositoryManifest> Manifests { get; init; } = new Dictionary<string, RepositoryManifest>();
    public DependencyFleetState? Fleet { get; init; }
    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// D-N5: the fleet view, the repository status and platform readiness, from the state the saga already keeps: the run
/// documents, the policy state (holds, approvals), the last run's report and the manifests (P12's lookup versions, else the
/// scanners' constraints). Nothing is fetched from a forge or a registry, and no version is parsed beyond R17's leading
/// segment (the readiness major).
/// </summary>
public static class DependencyFleetView
{
    /// <summary>The most package rows one view lists.</summary>
    public const int MaxPackages = 300;

    /// <summary>What readiness looks at: a package that moves a platform major with it, or an SDK's platform record.</summary>
    public static readonly IReadOnlyList<(string Platform, string Ecosystem, string DepName)> Anchors =
    [
        ("Angular", "npm", "@angular/core"),
        (".NET", "platform:dotnet", "platform:dotnet"),
        ("Node.js", "platform:node", "platform:node"),
        ("Python", "platform:python", "platform:python"),
        ("Java", "platform:java", "platform:java"),
        ("Go", "platform:go", "platform:go"),
    ];

    private static readonly HashSet<string> RedOutcomes = new(StringComparer.Ordinal) { "red", "bisected" };
    private static readonly HashSet<string> FrozenOutcomes = new(StringComparer.Ordinal) { "replay-conflict", "rolling-rewritten" };
    private static readonly HashSet<string> OkOutcomes = new(StringComparer.Ordinal)
    {
        "green", "published", "nothing-planned", "nothing-applied", "merged", "closed", "replay-retry", "disabled",
    };

    public static DependencyFleetResponse Build(DependencyFleetInput input)
    {
        var repositories = input.Repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var statuses = repositories.Select(r => Status(input, r)).ToList();
        var (packages, truncated) = Packages(input, repositories);
        return new DependencyFleetResponse(input.Account?.DependencyUpdates ?? false, statuses, packages, truncated, Readiness(input, repositories));
    }

    public static DependencyRepoView Repo(DependencyFleetInput input, Repository repository)
    {
        var report = input.Reports.GetValueOrDefault(repository.Id!);
        var run = input.Runs.GetValueOrDefault(repository.Id!);
        return new DependencyRepoView(Status(input, repository),
            [.. (report?.Groups ?? []).Select(g => new DependencyPlannedGroupView(g.Group, g.Level.ToString().ToLowerInvariant(), g.AutoMerge,
                [.. g.Items.Select(i => new DependencyPlannedItemView(i.DepName, i.PackageFile, i.From, i.To))]))],
            [.. (run?.History ?? []).TakeLast(15).Reverse().Select(e => new DependencyRunNote(e.AtUtc, e.Text))]);
    }

    public static DependencyRepoStatus Status(DependencyFleetInput input, Repository repository)
    {
        var id = repository.Id!;
        var run = input.Runs.GetValueOrDefault(id);
        var state = input.States.GetValueOrDefault(id);
        var report = input.Reports.GetValueOrDefault(id);
        var enabled = Repository.ResolveDependencyUpdates(repository, input.Account);
        var schedule = DependencySlots.Effective(repository, input.Account, run);
        DateTimeOffset? next = null;
        if (enabled && run?.FileDisabled != true && !repository.Archived)
        {
            // A slot whose run already started today is past: the next one is after this window.
            var (due, key) = DependencySlots.Due(schedule, input.Now, id);
            var ranThisSlot = due && run is { Trigger: EDependencyRunTrigger.Scheduled, StartedAtUtc: { } started }
                && DependencySlots.Due(schedule, new DateTimeOffset(DateTime.SpecifyKind(started, DateTimeKind.Utc)), id).Key == key;
            next = DependencySlots.Next(schedule, ranThisSlot ? input.Now.AddHours(1) : input.Now, id);
        }

        return new DependencyRepoStatus(
            id, repository.FullName, repository.Name, repository.IsPrivate, enabled, Health(enabled, run), (run?.State ?? EDependencyRunState.Idle).ToString(),
            run?.Outcome, run?.OutcomeMessage, run?.StartedAtUtc, run?.FinishedAtUtc, next, enabled ? Describe(schedule) : null,
            run?.PullRequestNumber is { } number ? new DependencyPullRequestLink(number, PullRequestUrl(repository, number), run.PullRequestIsDraft) : null,
            run?.Onboarding.ToString(), run?.OnboardingNote,
            [.. (state?.Holds ?? []).Select(h => new DependencyHoldView(h.Kind.ToString(), h.Group.Length == 0 ? null : h.Group, [.. h.DepNames], h.Reason, h.By, h.CreatedAtUtc))],
            [.. (report?.AwaitingApproval ?? []).Select(e => new DependencyAwaitingView(e.Group, e.DepName, e.Current, e.Target, $"approve {e.Group}"))],
            report?.Groups.Count ?? 0, [.. run?.Pins ?? []], run?.CoverageNote)
        {
            OnboardingPullRequest = run?.OnboardingPullRequestNumber is { } onboarding ? new DependencyPullRequestLink(onboarding, PullRequestUrl(repository, onboarding), false) : null,
            MergeQueueInFile = run?.FileMergeQueue,
            RateLimit = run?.RateLimitedUntilUtc is { } until && until > input.Now.UtcDateTime
                ? new DependencyRateLimitView(until, run.RateLimitParks, run.RateLimitParkBudget)
                : null,
        };
    }

    /// <summary>The chip's word: what a reader most needs to know first.</summary>
    public static string Health(bool enabled, DependencyUpdateRun? run)
    {
        if (!enabled)
            return "off";
        if (run is null)
            return "new";
        if (run.Onboarding == EDependencyOnboarding.Blocked)
            return "blocked";
        if (run.Onboarding == EDependencyOnboarding.PullRequestOpened)
            return "onboarding";
        if (run.State == EDependencyRunState.Bisecting)
            return "bisecting";
        if (run.State == EDependencyRunState.AwaitingChecks)
            return "waiting";
        if (run.State.IsBusy())
            return "running";
        if (run.Outcome is not { } outcome)
            return "new";
        if (RedOutcomes.Contains(outcome))
            return "red";
        if (FrozenOutcomes.Contains(outcome))
            return "frozen";
        // A dispatch whose run never reported back: no result, nothing to show for it (§8 "silent dispatches").
        if (outcome is "dispatch-uncorrelated" or "dispatch-refused" || outcome.EndsWith("-timeout", StringComparison.Ordinal) || outcome.EndsWith("-missing", StringComparison.Ordinal))
            return "silent";
        return OkOutcomes.Contains(outcome) ? "ok" : "failed";
    }

    private static string Describe(DependencySchedule schedule)
    {
        var hour = $"{schedule.Hour:00}:00 {schedule.TimeZone}";
        return schedule.Frequency switch
        {
            LookupReferences.EDependencyScheduleFrequency.Daily => $"daily at {hour}",
            LookupReferences.EDependencyScheduleFrequency.Monthly => $"first {schedule.Weekday} of the month at {hour}",
            _ => $"{schedule.Weekday}s at {hour}",
        };
    }

    internal static string? PullRequestUrl(Repository repository, int number)
        => repository.Provider == EForgeProvider.GitHub ? $"https://github.com/{repository.FullName}/pull/{number}" : null;

    // ── Packages ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The packages worth a row: used by two repositories or more, or with something going on in one (planned, held,
    /// awaiting approval, red). Most-used first; dev-only uses are listed, flagged.
    /// </summary>
    private static (IReadOnlyList<DependencyPackageRow> Rows, bool Truncated) Packages(DependencyFleetInput input, IReadOnlyList<Repository> repositories)
    {
        var visible = repositories.Select(r => r.Id!).ToHashSet(StringComparer.Ordinal);
        var red = (input.Fleet?.RedVersions ?? []).Where(r => visible.Contains(r.Repository)).ToList();
        var uses = new Dictionary<(string Ecosystem, string Name), List<DependencyPackageUse>>(KeyComparer.Instance);
        foreach (var repository in repositories)
        {
            if (input.Manifests.GetValueOrDefault(repository.Id!) is not { } manifest)
                continue;
            var report = input.Reports.GetValueOrDefault(repository.Id!);
            var state = input.States.GetValueOrDefault(repository.Id!);
            foreach (var dependency in DependencyLookupIngest.Consumes(manifest))
            {
                var key = (dependency.Ecosystem, dependency.Name);
                if (!uses.TryGetValue(key, out var list))
                    uses[key] = list = [];
                list.Add(new DependencyPackageUse(repository.Id!, repository.FullName, dependency.Version ?? dependency.Constraint, dependency.Path, dependency.Dev,
                    UseStatus(dependency, report, state, red.Where(r => r.Repository == repository.Id).ToList())));
            }
        }

        var qualified = uses
            .Where(u => u.Value.Select(x => x.RepositoryId).Distinct(StringComparer.Ordinal).Count() > 1 || u.Value.Any(x => x.Status is not null))
            .OrderByDescending(u => u.Value.Select(x => x.RepositoryId).Distinct(StringComparer.Ordinal).Count())
            .ThenBy(u => u.Key.Ecosystem, StringComparer.Ordinal)
            .ThenBy(u => u.Key.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = qualified.Take(MaxPackages)
            .Select(u => new DependencyPackageRow(u.Key.Ecosystem, u.Key.Name,
                [.. u.Value.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Path, StringComparer.Ordinal)]))
            .ToList();
        return (rows, qualified.Count > MaxPackages);
    }

    private static string? UseStatus(ManifestDependency dependency, DependencyRunReport? report, DependencyUpdateState? state, List<DependencyFleetRed> red)
    {
        bool Same(string depName) => Matches(dependency, depName);
        if (report?.Groups.SelectMany(g => g.Items).FirstOrDefault(i => Same(i.DepName) && i.PackageFile == dependency.Path) is { } planned)
            return $"in the pull request → {planned.NewVersion}";
        if (report?.AwaitingApproval.FirstOrDefault(e => Same(e.DepName)) is { } awaiting)
            return $"awaiting approval → {awaiting.Target}";
        if (state?.Holds.FirstOrDefault(h => h.DepNames.Any(Same)) is { } hold)
            return $"held: {hold.Reason}";
        if (report?.Held.FirstOrDefault(e => Same(e.DepName)) is { } held)
            return $"held: {held.Detail}";
        if (red.FirstOrDefault(r => Same(r.DepName)) is { } failed)
            return $"red: {failed.Version}";
        return null;
    }

    /// <summary>A lookup or hold name against a manifest's (normalized) one: the graph's own comparison, case-insensitive.</summary>
    private static bool Matches(ManifestDependency dependency, string depName)
        => DependencyLookupIngest.Ecosystem(dependency.Ecosystem) is { } ecosystem
           && string.Equals(dependency.Name, ecosystem == ManifestEcosystems.Pip ? PipManifestParser.Normalize(depName) : depName, StringComparison.OrdinalIgnoreCase);

    private sealed class KeyComparer : IEqualityComparer<(string Ecosystem, string Name)>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals((string Ecosystem, string Name) x, (string Ecosystem, string Name) y)
            => string.Equals(x.Ecosystem, y.Ecosystem, StringComparison.Ordinal) && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Ecosystem, string Name) obj) => HashCode.Combine(obj.Ecosystem, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    // ── Readiness ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per platform, its next major across the fleet (the highest major any lookup offered or any repository is on), and per
    /// repository whether it can move there:
    /// <list type="bullet">
    /// <item><c>on-target</c>: already on it; <c>in-pull-request</c>: planned in the rolling pull request;</item>
    /// <item><c>ready</c>: the lookup offers it and only an approval, the cooldown or an account rule stands in the way (the move
    /// itself is a rule change or an <c>approve</c>, §8);</item>
    /// <item><c>blocked</c>: the resolver refused it (R2: the blockers it named), or it went red in the fleet;</item>
    /// <item><c>held</c>: another hold (a person's, a comment's, the file's); <c>not-offered</c>: no lookup offered it yet.</item>
    /// </list>
    /// Ready counts on-target, in-pull-request and ready. Only platforms some repository is behind on are listed.
    /// </summary>
    private static IReadOnlyList<DependencyReadiness> Readiness(DependencyFleetInput input, IReadOnlyList<Repository> repositories)
    {
        var result = new List<DependencyReadiness>();
        foreach (var (platform, ecosystem, anchor) in Anchors)
        {
            var rows = new List<(Repository Repo, string? Current, long? CurrentMajor, Dictionary<long, (string Status, string? Detail, List<string> Blockers)> Offers)>();
            foreach (var repository in repositories)
            {
                var id = repository.Id!;
                var report = input.Reports.GetValueOrDefault(id);
                var state = input.States.GetValueOrDefault(id);
                var manifestUse = input.Manifests.GetValueOrDefault(id) is { } manifest
                    ? DependencyLookupIngest.Consumes(manifest).Where(d => d.Ecosystem == ecosystem && string.Equals(d.Name, anchor, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(d => d.Dev).FirstOrDefault()
                    : null;
                var entries = report is null ? [] : report.AwaitingApproval.Concat(report.Held).Concat(report.Available).Where(e => e.DepName == anchor).ToList();
                var planned = report?.Groups.SelectMany(g => g.Items).Where(i => i.DepName == anchor).ToList() ?? [];
                var current = manifestUse?.Version ?? manifestUse?.Constraint ?? entries.Select(e => e.Current).FirstOrDefault(c => c is not null) ?? planned.Select(p => p.From).FirstOrDefault();
                if (current is null && entries.Count == 0 && planned.Count == 0)
                    continue;

                var offers = new Dictionary<long, (string, string?, List<string>)>();
                foreach (var item in planned)
                    if (Major(item.NewVersion) is { } major)
                        offers[major] = ("in-pull-request", null, []);
                foreach (var entry in entries)
                {
                    if (Major(entry.Target) is not { } major || offers.ContainsKey(major))
                        continue;
                    offers[major] = entry.Reason switch
                    {
                        EDependencyReportReason.AwaitingApproval or EDependencyReportReason.AboveAccountRule or EDependencyReportReason.Cooldown
                            or EDependencyReportReason.NotInSecurityRun => ("ready", entry.Detail, []),
                        EDependencyReportReason.FleetRed => ("blocked", entry.Detail, []),
                        EDependencyReportReason.Held when state?.Holds.FirstOrDefault(h => h.Kind == EDependencyHoldKind.Blocker
                            && (h.Group == entry.Group || h.DepNames.Contains(anchor))) is { } blocker
                            => ("blocked", blocker.Reason, [.. blocker.Blockers.Select(b => b.DepName)]),
                        _ => ("held", entry.Detail, []),
                    };
                }
                rows.Add((repository, current, Major(current), offers));
            }
            if (rows.Count == 0)
                continue;

            var known = rows.SelectMany(r => r.Offers.Keys).Concat(rows.Select(r => r.CurrentMajor).OfType<long>()).ToList();
            if (known.Count == 0)
                continue;
            var next = known.Max();
            if (rows.All(r => r.CurrentMajor >= next))
                continue;

            var repos = rows.Select(r =>
            {
                if (r.CurrentMajor >= next)
                    return new DependencyReadinessRepo(r.Repo.Id!, r.Repo.FullName, r.Current, "on-target", null, []);
                return r.Offers.TryGetValue(next, out var offer)
                    ? new DependencyReadinessRepo(r.Repo.Id!, r.Repo.FullName, r.Current, offer.Status, offer.Detail, offer.Blockers)
                    : new DependencyReadinessRepo(r.Repo.Id!, r.Repo.FullName, r.Current, "not-offered", "no lookup has offered it for this repository yet", []);
            }).OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            var ready = repos.Count(r => r.Status is "on-target" or "in-pull-request" or "ready");
            var blockers = repos.SelectMany(r => r.Blockers.Distinct(StringComparer.Ordinal).Select(b => (b, r.RepositoryId)))
                .GroupBy(x => x.b, StringComparer.Ordinal)
                .Select(g => new DependencyBlockerCount(g.Key, g.Select(x => x.RepositoryId).Distinct(StringComparer.Ordinal).Count()))
                .OrderByDescending(b => b.Repositories).ThenBy(b => b.Name, StringComparer.Ordinal)
                .ToList();
            result.Add(new DependencyReadiness(platform, anchor, next.ToString(System.Globalization.CultureInfo.InvariantCulture), ready, repos.Count, repos, blockers));
        }
        return result;
    }

    /// <summary>R17's leading segment as a number: the one reading of a version the bot allows itself.</summary>
    private static long? Major(string? token) => token is null ? null : UpdateTypeFloor.LeadingNumber(token);
}
