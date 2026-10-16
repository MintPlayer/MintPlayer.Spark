using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using YamlDotNet.RepresentationModel;

namespace CodeCoverage.Dependencies.Lifecycle;

// The decisions of the dependency saga that need no database and no forge: pure functions, unit-tested on their own.

/// <summary>What a run's policy decided, as stored for the pull request body and the commands (approve, hold).</summary>
public sealed record DependencyRunReport
{
    public List<DependencyReportGroup> Groups { get; init; } = [];
    public List<DependencyReportEntry> Held { get; init; } = [];
    public List<DependencyReportEntry> AwaitingApproval { get; init; } = [];
    public List<DependencyReportEntry> Available { get; init; } = [];
    public List<DependencyReportEntry> Ignored { get; init; } = [];
    public List<DependencyPlatformReport> PlatformReports { get; init; } = [];
    public List<string> LiftedHolds { get; init; } = [];
    public List<string> ExpiredApprovals { get; init; } = [];

    /// <summary>Groups the apply run could not apply, with why (R2; "no adapter" ones listed apart).</summary>
    public List<DependencyReportFailure> Failures { get; init; } = [];

    /// <summary>R19's blocking notes and the migrations' extra bumps.</summary>
    public List<string> MigrationNotes { get; init; } = [];

    /// <summary>Gap B: the lockfiles bring in more new packages than the cap.</summary>
    public string? NeedsHumanDetail { get; init; }

    /// <summary>D-N1: dependencies kept at the version a human fix on the rolling branch was made against.</summary>
    public List<DependencyPin> Pins { get; init; } = [];

    public static DependencyRunReport From(DependencyPolicyOutcome outcome, IReadOnlyCollection<string> dropped) => new()
    {
        Groups = [.. outcome.Groups.Where(g => !dropped.Contains(g.Group)).Select(DependencyReportGroup.From)],
        Held = [.. outcome.Held],
        AwaitingApproval = [.. outcome.AwaitingApproval],
        Available = [.. outcome.Available],
        Ignored = [.. outcome.Ignored],
        PlatformReports = [.. outcome.PlatformReports],
        LiftedHolds = [.. outcome.LiftedHolds.Select(h => h.Reason)],
        ExpiredApprovals = [.. outcome.ExpiredApprovals.Select(a => a.Group)],
    };

    /// <summary>The awaiting-approval half as the policy outcome <see cref="DependencyHolds.Approve"/> reads (R6, gap E).</summary>
    public DependencyPolicyOutcome AsOutcome(DependencyUpdateState state) => new() { AwaitingApproval = AwaitingApproval, NextState = state };
}

public sealed record DependencyReportGroup(string Group, EUpdateLevel Level, bool AutoMerge, bool Approved, bool Security, string? MigrateWorkflow,
    bool Platform, List<DependencyReportItem> Items)
{
    public static DependencyReportGroup From(DependencyPlannedGroup group) => new(group.Group, group.Level, group.AutoMerge, group.Approved, group.Security,
        group.MigrateWorkflow, group.Platform is not null,
        group.Platform is { } platform
            ? [new DependencyReportItem($"platform:{platform.Sdk}", "(platform)", $"platform:{platform.Sdk}", "platform", platform.FromChannel, platform.ToChannel, platform.ToVersion ?? platform.ToChannel, null)]
            : [.. group.Items.Select(i => new DependencyReportItem(i.Record.DepName, i.Record.PackageFile, i.Record.Ecosystem, i.Record.Manager,
                i.Record.CurrentVersion ?? i.Record.CurrentValue ?? string.Empty, i.Candidate.Value ?? i.Record.CurrentValue ?? string.Empty, i.Candidate.Version ?? string.Empty,
                i.Candidate.ReleaseTimestamp))]);
}

public sealed record DependencyReportItem(string DepName, string PackageFile, string Ecosystem, string Manager, string From, string To, string NewVersion, string? ReleaseTimestamp);

public sealed record DependencyReportFailure(string Group, string Status, string? Message, bool NoAdapter);

/// <summary>The scheduled slot (Q8): frequency, weekday, hour in the account's time zone, spread within the hour.</summary>
public static class DependencySlots
{
    /// <summary>The minute of the hour a repository's run starts at, from its id: repositories at the same slot are
    /// staggered across the hour instead of dispatching at once (§7.7).</summary>
    public static int SpreadMinute(string repositoryId)
        => BitConverter.ToUInt16(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryId)), 0) % 60;

    /// <summary>
    /// Whether the slot is due at <paramref name="nowUtc"/>, and its deduplication key: one run per repository per slot, however
    /// often the scheduler looks. Due from the spread minute to the end of the slot hour, so a scheduler that runs every few
    /// minutes still finds it.
    /// </summary>
    public static (bool Due, string Key) Due(DependencySchedule schedule, DateTimeOffset nowUtc, string repositoryId)
    {
        var zone = Zone(schedule.TimeZone);
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var key = $"dependency-slot:{repositoryId}:{local:yyyy-MM-dd}";
        return (IsSlotDay(schedule, local.DateTime) && local.Hour == schedule.Hour && local.Minute >= SpreadMinute(repositoryId), key);
    }

    private static bool IsSlotDay(DependencySchedule schedule, DateTime local) => schedule.Frequency switch
    {
        EDependencyScheduleFrequency.Daily => true,
        EDependencyScheduleFrequency.Monthly => IsWeekday(local, schedule.Weekday) && local.Day <= 7,
        _ => IsWeekday(local, schedule.Weekday),
    };

    /// <summary>The schedule the cron uses for a repository (D-L13): the settings, overridden part by part by the
    /// <c>coverage.yml</c> schedule the last run read.</summary>
    public static DependencySchedule Effective(Repository repository, Account? account, DependencyUpdateRun? run)
    {
        var (settings, _) = Repository.ResolveDependencySchedule(repository, account);
        return settings with
        {
            Frequency = run?.FileFrequency ?? settings.Frequency,
            Weekday = run?.FileWeekday ?? settings.Weekday,
            Hour = run?.FileHour ?? settings.Hour,
        };
    }

    /// <summary>
    /// When the slot is next due at or after <paramref name="fromUtc"/>: the start of the next slot window (its spread
    /// minute), or <paramref name="fromUtc"/> itself while a window is open. Null when none falls within
    /// <paramref name="horizonDays"/> (the three frequencies repeat within 35 days). Uses the same day and hour rule as
    /// <see cref="Due"/>, local hour by local hour.
    /// </summary>
    public static DateTimeOffset? Next(DependencySchedule schedule, DateTimeOffset fromUtc, string repositoryId, int horizonDays = 40)
    {
        var zone = Zone(schedule.TimeZone);
        var minute = SpreadMinute(repositoryId);
        var local = TimeZoneInfo.ConvertTime(fromUtc, zone).DateTime;
        var hour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
        for (var i = 0; i < horizonDays * 24; i++)
        {
            var h = hour.AddHours(i);
            if (h.Hour != schedule.Hour || !IsSlotDay(schedule, h))
                continue;
            var opens = h.AddMinutes(minute);
            var closes = h.AddHours(1);
            if (zone.IsInvalidTime(opens) || zone.IsInvalidTime(closes))
                continue;
            var opensUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(opens, zone));
            var closesUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(closes, zone));
            if (closesUtc <= fromUtc)
                continue;
            return opensUtc > fromUtc ? opensUtc : fromUtc;
        }
        return null;
    }

    private static bool IsWeekday(DateTime local, EDependencyWeekday weekday) => weekday switch
    {
        EDependencyWeekday.Monday => local.DayOfWeek == DayOfWeek.Monday,
        EDependencyWeekday.Tuesday => local.DayOfWeek == DayOfWeek.Tuesday,
        EDependencyWeekday.Wednesday => local.DayOfWeek == DayOfWeek.Wednesday,
        EDependencyWeekday.Thursday => local.DayOfWeek == DayOfWeek.Thursday,
        EDependencyWeekday.Friday => local.DayOfWeek == DayOfWeek.Friday,
        EDependencyWeekday.Saturday => local.DayOfWeek == DayOfWeek.Saturday,
        _ => local.DayOfWeek == DayOfWeek.Sunday,
    };

    /// <summary>An IANA id; an unknown one reads as UTC (D-P16).</summary>
    public static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

public enum ECoverageGate
{
    /// <summary>No coverage for the head: the gate is skipped (R11).</summary>
    NoData,

    /// <summary>Within <c>maxCoverageDrop</c>.</summary>
    Passed,

    /// <summary>A drop beyond <c>maxCoverageDrop</c>: no auto-merge.</summary>
    Dropped,

    /// <summary>The report is incomplete (missing uploads): its own hold, never read as a drop (R11).</summary>
    Incomplete,
}

/// <summary>R11: the coverage delta of the rolling head against the default branch, and whether auto-merge may go on.</summary>
public static class DependencyCoverageGate
{
    public static (ECoverageGate Gate, string Note) Evaluate(Commit? head, double maxCoverageDrop)
    {
        if (head?.Coverage is null)
            return (ECoverageGate.NoData, "No coverage was reported for this head; the coverage condition is skipped.");
        if (head.AssemblyCompleteness == CommitAssembly.Partial)
            return (ECoverageGate.Incomplete, "The coverage report is incomplete (uploads are missing), so this cannot auto-merge until it is complete.");
        if (head.CoverageDeltaVsDefaultBranch is not { } delta)
            return (ECoverageGate.NoData, "There is no default-branch coverage to compare with; the coverage condition is skipped.");
        var text = delta.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        return delta >= -Math.Abs(maxCoverageDrop)
            ? (ECoverageGate.Passed, $"Coverage {text} pp against the default branch.")
            : (ECoverageGate.Dropped, $"Coverage {text} pp against the default branch, beyond the allowed drop of {Math.Abs(maxCoverageDrop).ToString("0.00", CultureInfo.InvariantCulture)} pp.");
    }
}

/// <summary>Everything R5, R11, R15 and R16 ask before the rolling pull request may enter the merge queue.</summary>
public sealed record DependencyEnqueueInput(
    bool MergeQueueEnabled,
    DependencyRunReport Report,
    bool IsDraft,
    bool NeedsHuman,
    bool HasMigrations,
    IReadOnlyList<string> WorkflowsNotOnPullRequest,
    ECoverageGate Coverage);

public static class DependencyAutoMergeGate
{
    /// <summary>Null when the pull request may be enqueued, else why not (shown in the body).</summary>
    public static string? Refusal(DependencyEnqueueInput input)
    {
        if (!input.MergeQueueEnabled)
            return "Auto-merge needs the merge queue (`mergeQueue: { enabled: true }` in coverage.yml); this pull request waits for a person.";
        if (input.IsDraft)
            return "This is the first pull request after onboarding: a draft, merged by a person.";
        if (input.Report.Groups.Count == 0)
            return "Nothing is applied.";
        if (input.Report.Groups.FirstOrDefault(g => !g.AutoMerge) is { } over)
            return $"`{over.Group}` is above its auto-merge threshold{(over.Approved ? " (approved by a comment)" : string.Empty)}.";
        if (input.Report.Groups.FirstOrDefault(g => g.Platform && g.Level == EUpdateLevel.Major) is { } platform)
            return $"`{platform.Group}` moves a platform major, which never auto-merges.";
        if (input.HasMigrations)
            return "It contains code migrations, which never auto-merge.";
        if (input.NeedsHuman)
            return "The lockfiles bring in more new packages than the bot may merge without a person.";
        if (input.Report.MigrationNotes.Count > 0)
            return "A migration introduced a held version.";
        if (input.WorkflowsNotOnPullRequest.Count > 0)
            return $"It edits {string.Join(", ", input.WorkflowsNotOnPullRequest.Select(w => $"`{w}`"))}, which no pull_request run tests.";
        return input.Coverage switch
        {
            ECoverageGate.Incomplete => "The coverage report is incomplete.",
            ECoverageGate.Dropped => "Coverage dropped beyond `dependencies.autoMerge.maxCoverageDrop`.",
            _ => null,
        };
    }
}

/// <summary>What kind of failure a red head shows, as far as the failing checks' names tell.</summary>
public enum ECheckFailureKind
{
    /// <summary>No failing check's name says.</summary>
    Unknown,

    /// <summary>Only checks named for building (build, compile, restore, lint, typecheck, pack).</summary>
    Build,

    /// <summary>Only checks named for testing (test, e2e, spec, integration).</summary>
    Test,

    /// <summary>Both kinds failed, or one check is named for both (<c>build-and-test</c>).</summary>
    Both,
}

/// <summary>
/// Old §7.4's "build failure vs test failure", read from the failing check runs' <b>names</b> only (dependency-updates PRD
/// §13.12 D-N3). The bot reads no logs and no annotations: a log is repository output a runner produced, and reading it would
/// need the Actions log API per job for a hint that only changes the comment's wording. So a check named <c>ci</c> is
/// <see cref="ECheckFailureKind.Unknown"/>, and the bisect is the same either way.
/// </summary>
public static class DependencyCheckFailure
{
    private static readonly string[] BuildWords = ["build", "compile", "restore", "lint", "typecheck", "type-check", "tsc", "pack"];
    private static readonly string[] TestWords = ["test", "e2e", "spec", "integration", "playwright", "cypress", "vitest", "jest"];

    public static ECheckFailureKind Classify(IEnumerable<string> failingChecks)
    {
        bool build = false, test = false;
        foreach (var name in failingChecks)
        {
            // Words, not substrings: "build (ubuntu-latest)" holds "test" inside "latest", and "contest" or "protest"
            // would too. A word starts a token ("tests", "linter", "compiler"); a hyphenated word matches as written.
            var lower = name.ToLowerInvariant();
            var tokens = lower.Split(NonWord, StringSplitOptions.RemoveEmptyEntries);
            build |= BuildWords.Any(w => Matches(lower, tokens, w));
            test |= TestWords.Any(w => Matches(lower, tokens, w));
        }
        return (build, test) switch
        {
            (true, true) => ECheckFailureKind.Both,
            (true, false) => ECheckFailureKind.Build,
            (false, true) => ECheckFailureKind.Test,
            _ => ECheckFailureKind.Unknown,
        };
    }

    private static readonly char[] NonWord = [.. Enumerable.Range(0, 128).Select(c => (char)c).Where(c => !char.IsAsciiLetterOrDigit(c))];

    private static bool Matches(string lower, string[] tokens, string word)
        => word.Contains('-')
            ? lower.Contains(word, StringComparison.Ordinal)
            : tokens.Any(t => t.StartsWith(word, StringComparison.Ordinal));

    /// <summary>A few words for the pull request comment; empty when the names say nothing.</summary>
    public static string Describe(ECheckFailureKind kind) => kind switch
    {
        ECheckFailureKind.Build => "a build check failed (the update likely breaks compilation or restore)",
        ECheckFailureKind.Test => "a test check failed (it builds; behaviour changed)",
        ECheckFailureKind.Both => "build and test checks failed",
        _ => string.Empty,
    };
}

/// <summary>Old §7.4's suspicion order and the next commit to test.</summary>
public static class DependencyBisectPlanner
{
    /// <summary>
    /// The groups ranked by suspicion: majors first, then a group with a version red elsewhere in the fleet, then the rest;
    /// within a rank the later plan groups first (the plan runs patch → minor → major, D-P10). Failing stack traces are not
    /// read (the bot has no log access in this milestone).
    /// </summary>
    public static List<string> Suspects(DependencyRunReport report, DependencyFleetState fleet)
    {
        var red = fleet.RedVersions.Select(r => (r.Ecosystem, r.DepName, r.Version)).ToHashSet();
        return [.. report.Groups
            .Select((g, i) => (Group: g, Index: i))
            .OrderByDescending(x => x.Group.Level == EUpdateLevel.Major)
            .ThenByDescending(x => x.Group.Items.Any(item => red.Contains((item.Ecosystem, item.DepName, item.NewVersion))))
            .ThenByDescending(x => x.Index)
            .Select(x => x.Group.Group)];
    }

    /// <summary>The groups the next test leaves out (cumulative: one more suspect each step), or null when the budget or the
    /// suspects are used up. One step is one CI run; the re-run counts too.</summary>
    public static List<string>? NextExclusion(DependencyBisect bisect)
    {
        if (bisect.Spent >= bisect.Budget || bisect.Excluded.Count >= bisect.Suspects.Count - 1)
            return null;
        return [.. bisect.Suspects.Take(bisect.Excluded.Count + 1)];
    }
}

/// <summary>
/// Gap E's own-package check, the three legs: a repository of the same account declares the package (the producer graph,
/// <see cref="RepositoryManifest.Produces"/>), and the registry's publish time (the lookup's <c>releaseTimestamp</c>) lies
/// within <see cref="Window"/> of a release or push event the bot received from that repository. A look-alike published from
/// elsewhere has no such event, so it waits out the cooldown.
/// </summary>
public static class OwnReleaseCheck
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(2);

    public static IReadOnlyList<DependencyOwnRelease> Confirm(LookupDocument lookup, IReadOnlyList<RepositoryManifest> producers,
        IReadOnlyList<DependencyProducerEvent> events, DateTimeOffset now)
    {
        var found = new List<DependencyOwnRelease>();
        foreach (var record in lookup.Records)
        {
            var names = new[] { record.DepName, record.PackageName }.OfType<string>().ToHashSet(StringComparer.Ordinal);
            var producer = producers.FirstOrDefault(p => p.Produces.Any(pkg => names.Contains(pkg.Name)
                && string.Equals(pkg.Ecosystem, record.Ecosystem, StringComparison.OrdinalIgnoreCase)));
            if (producer?.Repository is not { } producerRepository)
                continue;
            foreach (var candidate in record.Candidates)
            {
                if (candidate.Version is not { } version || !DateTimeOffset.TryParse(candidate.ReleaseTimestamp, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var published))
                    continue;
                var correlated = events.Any(e => e.ProducerRepository == producerRepository
                    && (published - new DateTimeOffset(DateTime.SpecifyKind(e.AtUtc, DateTimeKind.Utc))).Duration() <= Window);
                if (correlated && !found.Any(f => f.Ecosystem == record.Ecosystem && f.DepName == record.DepName && f.Version == version))
                    found.Add(new DependencyOwnRelease { Ecosystem = record.Ecosystem, DepName = record.DepName, Version = version, ProducerRepository = producerRepository, AtUtc = now.UtcDateTime });
            }
        }
        return found;
    }
}

/// <summary>
/// R12 / T5 for the workflows the bot dispatches, the repository's own included: an explicit top-level <c>permissions:</c>
/// that writes nothing, no <c>id-token</c>, no <c>pull_request_target</c> trigger, and for a migrate workflow no secrets at all
/// (S17: migrations run package code by design). Lookup and apply may read feed secrets (P2, D-S14b), so secrets are not
/// refused there.
/// </summary>
public static class DependencyWorkflowGuard
{
    public static string? Refusal(string? text, bool allowSecrets)
    {
        if (text is null)
            return "it could not be read";
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
                return "it is not one YAML mapping";
            root = mapping;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            return $"it is not valid YAML ({ex.Message})";
        }

        if (!root.Children.TryGetValue(new YamlScalarNode("permissions"), out var permissions))
            return "it has no top-level `permissions:`";
        if (WritesAnything(permissions))
            return "its top-level `permissions:` grant write access or `id-token`";
        if (root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobs) && jobs is YamlMappingNode jobMap)
        {
            foreach (var job in jobMap.Children.Values.OfType<YamlMappingNode>())
            {
                if (job.Children.TryGetValue(new YamlScalarNode("permissions"), out var jobPermissions) && WritesAnything(jobPermissions))
                    return "a job's `permissions:` grant write access or `id-token`";
            }
        }
        if (root.Children.TryGetValue(new YamlScalarNode("on"), out var on) && Triggers(on).Contains("pull_request_target"))
            return "it runs on `pull_request_target`";
        if (!allowSecrets && text.Contains("secrets.", StringComparison.Ordinal))
            return "it reads repository secrets";
        return null;
    }

    private static bool WritesAnything(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value is "write-all",
        YamlMappingNode map => map.Children.Any(p => p.Value is YamlScalarNode v && (v.Value == "write" || ((YamlScalarNode)p.Key).Value == "id-token" && v.Value != "none")),
        _ => true,
    };

    /// <summary>The event names of an <c>on:</c> value: a scalar, a sequence or a mapping.</summary>
    public static IReadOnlySet<string> Triggers(YamlNode on) => on switch
    {
        YamlScalarNode scalar when scalar.Value is { } value => new HashSet<string>([value], StringComparer.Ordinal),
        YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value).OfType<string>().ToHashSet(StringComparer.Ordinal),
        YamlMappingNode map => map.Children.Keys.OfType<YamlScalarNode>().Select(s => s.Value).OfType<string>().ToHashSet(StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    /// <summary>R15: whether a workflow runs on <c>pull_request</c>, so a <c>uses:</c>/<c>with:</c> edit of it is tested by the
    /// pull request's own CI before it merges. An <c>action.yml</c> is judged by its callers, so it counts as tested.</summary>
    public static bool RunsOnPullRequest(string path, string? text)
    {
        if (!path.StartsWith(".github/workflows/", StringComparison.Ordinal))
            return true;
        if (text is null)
            return false;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            return stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlMappingNode root
                && root.Children.TryGetValue(new YamlScalarNode("on"), out var on) && Triggers(on).Contains("pull_request");
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }
}
