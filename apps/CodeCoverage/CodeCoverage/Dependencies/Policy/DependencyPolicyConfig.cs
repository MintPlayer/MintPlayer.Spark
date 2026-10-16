using CodeCoverage.Entities;
using CodeCoverage.Git;
using CodeCoverage.LookupReferences;

namespace CodeCoverage.Dependencies.Policy;

/// <summary>
/// Everything the policy engine is configured by, combined once (PRD §4.2, R6, R7, R15, Q9): the file half
/// (<see cref="DependenciesSection"/>, read from the default branch), the account's rules and the owner's settings.
/// </summary>
/// <remarks>
/// <para><b>Threshold</b> (the largest update planned and auto-merged without an approval), most specific first:</para>
/// <list type="number">
/// <item>the file's <c>autoMerge</c> globs (the most specific matching glob wins);</item>
/// <item>a non-enforced account rule, when no file glob or rule names the dependency (§4.2: "a default that a repo rule
/// replaces");</item>
/// <item>the built-in globs (<c>@angular/*: never</c>, <c>actions/*: minor</c>), never above the file's default;</item>
/// <item>the ecosystem defaults: <c>github-actions</c> and <c>platform:*</c> stop at minor (R15, R7);</item>
/// <item>the file's <c>autoMerge.default</c> (built-in minor).</item>
/// </list>
/// <para>
/// Then the <b>enforced account rules are a ceiling</b> (Q9, R6): the threshold, <c>allowMajor</c> and any approval stay at
/// or under the lowest matching enforced <see cref="DependencyRule.MaxUpdate"/>; an enforced <c>ignore</c> cannot be undone.
/// </para>
/// </remarks>
public sealed class DependencyPolicyConfig
{
    public DependenciesSection File { get; init; } = DependenciesSection.Defaults();

    public IReadOnlyList<DependencyRule> AccountRules { get; init; } = [];

    /// <summary>The owner's cooldown (Q16), before the file's <c>cooldown</c> lengthens it.</summary>
    public TimeSpan SettingsCooldown { get; init; } = TimeSpan.FromDays(3);

    /// <summary>R17 guard 2: managers whose lookup executes repository code get no auto-merge and no cooldown bypass. S12
    /// found none (35 planted hooks, 0 sentinels), so the list is empty; it is re-checked on every Renovate digest bump.</summary>
    public IReadOnlySet<string> RepoCodeManagers { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The cooldown in force: the settings' value, or the file's when that is longer.</summary>
    public TimeSpan Cooldown => File.MinimumCooldown is { } file && file > SettingsCooldown ? file : SettingsCooldown;

    /// <summary>The configuration of one repository: its default branch's file plus its owner's settings and rules.</summary>
    public static DependencyPolicyConfig Resolve(DependenciesSection file, Repository? repository, Account? account) => new()
    {
        File = file,
        AccountRules = [.. (account?.DependencyRules ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Match))],
        SettingsCooldown = Repository.ResolveDependencyCooldown(repository, account).Cooldown,
    };

    /// <summary>The effective schedule: the settings' slot with the file's <c>schedule</c> parts on top.</summary>
    public static DependencySchedule Schedule(DependenciesSection file, Repository? repository, Account? account)
    {
        var (schedule, _) = Repository.ResolveDependencySchedule(repository, account);
        return file.Schedule is not { } over
            ? schedule
            : schedule with
            {
                Frequency = over.Frequency ?? schedule.Frequency,
                Weekday = over.Weekday ?? schedule.Weekday,
                Hour = over.Hour ?? schedule.Hour,
            };
    }

    /// <summary>Whether the repository takes part: the settings opt in, the file may only opt out (Q6).</summary>
    public static bool Enabled(DependenciesSection file, Repository? repository, Account? account)
        => Repository.ResolveDependencyUpdates(repository, account) && file.Enabled;

    /// <summary>The package file lies under a <c>dependencies.exclude</c> glob.</summary>
    public bool IsExcludedPath(string packageFile) => File.Exclude.Any(glob => new PathGlob(glob).Matches(packageFile));

    /// <summary>Why the dependency is ignored (an enforced account rule, the file, a non-enforced account rule), or null.</summary>
    public string? IgnoredBy(string ecosystem, string depName)
    {
        if (Enforced(ecosystem, depName).FirstOrDefault(r => r.Ignore) is { } enforced)
            return $"ignored by the account rule '{enforced.Match}'";
        var fileRule = FileRule(ecosystem, depName);
        if (fileRule is { Ignore: true })
            return $"ignored by coverage.yml ('{string.Join("', '", fileRule.Match)}')";
        if (fileRule is null && !FileNames(ecosystem, depName) && Defaults(ecosystem, depName).FirstOrDefault(r => r.Ignore) is { } fallback)
            return $"ignored by the account rule '{fallback.Match}'";
        return null;
    }

    /// <summary>A <c>hold</c> of the file's first rule naming the dependency, or null.</summary>
    public string? FileHold(string ecosystem, string depName) => FileRule(ecosystem, depName)?.Hold;

    /// <summary>The configured group: the file's rule, else an account rule's; null leaves the lookup's grouping (D-C1).</summary>
    public string? ConfiguredGroup(string ecosystem, string depName)
        => FileRules(ecosystem, depName).Select(r => r.Group).FirstOrDefault(g => g is not null)
            ?? Enforced(ecosystem, depName).Concat(Defaults(ecosystem, depName)).Select(r => r.Group).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g))?.Trim();

    /// <summary>The largest update planned and auto-merged without an approval, ceiling applied.</summary>
    public EDependencyThreshold Threshold(string ecosystem, string depName) => Min(RawThreshold(ecosystem, depName), Ceiling(ecosystem, depName));

    /// <summary>The largest update planned without an approval: the threshold, or major under a file rule's
    /// <c>allowMajor: true</c>; never above the ceiling.</summary>
    public EDependencyThreshold Inclusion(string ecosystem, string depName)
    {
        var allowMajor = FileRules(ecosystem, depName).Select(r => r.AllowMajor).FirstOrDefault(a => a is not null) ?? false;
        return Min(allowMajor ? EDependencyThreshold.Major : RawThreshold(ecosystem, depName), Ceiling(ecosystem, depName));
    }

    /// <summary>The lowest enforced account <see cref="DependencyRule.MaxUpdate"/> naming the dependency; major when none.
    /// Nothing, an approval included, admits an update above it.</summary>
    public EDependencyThreshold Ceiling(string ecosystem, string depName)
        => Enforced(ecosystem, depName).Select(r => r.MaxUpdate).DefaultIfEmpty(EDependencyThreshold.Major).Min();

    /// <summary>The <c>migrate</c> workflow for an approved major of the dependency (R9), or null.</summary>
    public string? MigrateWorkflow(string depName)
        => File.Migrate.FirstOrDefault(m => m.Match.Any(glob => DependencyGlob.Matches(glob, depName)))?.OnMajor;

    private EDependencyThreshold RawThreshold(string ecosystem, string depName)
    {
        if (MostSpecific(File.AutoMerge, depName) is { } fileGlob)
            return fileGlob.Threshold;
        if (FileRule(ecosystem, depName) is null && Defaults(ecosystem, depName).OrderByDescending(r => DependencyGlob.Specificity(r.Match)).FirstOrDefault() is { } fallback)
            return fallback.MaxUpdate;
        // A built-in only ever lowers the file's default: it is a safety default, never a widening.
        if (MostSpecific(DependenciesSection.BuiltInThresholds, depName) is { } builtIn)
            return Min(builtIn.Threshold, File.DefaultThreshold);
        if (ecosystem == "github-actions" || ecosystem.StartsWith("platform:", StringComparison.Ordinal))
            return Min(File.DefaultThreshold, EDependencyThreshold.Minor);
        return File.DefaultThreshold;
    }

    private static DependencyThresholdGlob? MostSpecific(IEnumerable<DependencyThresholdGlob> globs, string depName)
        => globs.Select((g, i) => (g, i))
            .Where(x => DependencyGlob.Matches(x.g.Glob, depName))
            .OrderByDescending(x => DependencyGlob.Specificity(x.g.Glob)).ThenBy(x => x.i)
            .Select(x => x.g).FirstOrDefault();

    private IEnumerable<DependencyFileRule> FileRules(string ecosystem, string depName)
        => File.Rules.Where(r => (r.Ecosystem is null || r.Ecosystem == ecosystem) && r.Match.Any(glob => DependencyGlob.Matches(glob, depName)));

    private DependencyFileRule? FileRule(string ecosystem, string depName) => FileRules(ecosystem, depName).FirstOrDefault();

    /// <summary>The file names the dependency in an <c>autoMerge</c> glob.</summary>
    private bool FileNames(string ecosystem, string depName) => MostSpecific(File.AutoMerge, depName) is not null;

    private IEnumerable<DependencyRule> AccountMatches(string ecosystem, string depName)
        => AccountRules.Where(r => (string.IsNullOrWhiteSpace(r.Ecosystem) || r.Ecosystem.Trim() == ecosystem) && DependencyGlob.Matches(r.Match.Trim(), depName));

    private IEnumerable<DependencyRule> Enforced(string ecosystem, string depName) => AccountMatches(ecosystem, depName).Where(r => r.Enforced);

    private IEnumerable<DependencyRule> Defaults(string ecosystem, string depName) => AccountMatches(ecosystem, depName).Where(r => !r.Enforced);

    internal static EDependencyThreshold Min(EDependencyThreshold a, EDependencyThreshold b) => a <= b ? a : b;
}
