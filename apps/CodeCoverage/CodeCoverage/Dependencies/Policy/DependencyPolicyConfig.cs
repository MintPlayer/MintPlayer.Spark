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

    /// <summary>
    /// The track an SDK (and its companions) is offered from (#606 D5/D8): the file's <c>platforms.&lt;sdk&gt;.track</c>, else a
    /// non-enforced account rule's on <c>platform:&lt;sdk&gt;</c>, else stable; never wider than an enforced rule's track. A
    /// resolved <c>lts</c> on an SDK whose feed flags no LTS release falls back to stable.
    /// </summary>
    public EDependencyTrack PlatformTrack(string sdk)
    {
        var ecosystem = $"platform:{sdk}";
        var chosen = File.Platforms.TryGetValue(sdk, out var settings) && settings.Track != EDependencyTrack.Inherit
            ? settings.Track
            : TrackOf(Defaults(ecosystem, sdk)) ?? EDependencyTrack.Stable;
        var track = MinTrack(chosen, TrackOf(Enforced(ecosystem, sdk)));
        return track == EDependencyTrack.Lts && !DependenciesYml.LtsSdks.Contains(sdk) ? EDependencyTrack.Stable : track;
    }

    /// <summary>The track a package is offered from; see <see cref="PackageTrackOf"/>.</summary>
    public EDependencyTrack PackageTrack(string ecosystem, string depName, string? currentVersion = null)
        => PackageTrackOf(ecosystem, depName, currentVersion).Track;

    /// <summary>
    /// The track a package is offered from, and the setting that decides it (for the report line of a prerelease it is not
    /// offered). #606 D8: the file's first rule naming it with a track, else a non-enforced account rule's. Without either,
    /// prerelease-dependencies-PRD H1: preview when the current version is a prerelease (in a SemVer ecosystem, H5) or the
    /// package is a companion of an SDK on the preview track, stable otherwise. Never wider than an enforced rule's track.
    /// <c>lts</c> has no meaning for a package and reads as stable.
    /// </summary>
    public (EDependencyTrack Track, string Why) PackageTrackOf(string ecosystem, string depName, string? currentVersion)
    {
        EDependencyTrack chosen;
        string why;
        var file = FileRules(ecosystem, depName).Select(r => r.Track).FirstOrDefault(t => t != EDependencyTrack.Inherit);
        if (file != EDependencyTrack.Inherit)
            (chosen, why) = (file, $"a coverage.yml rule sets track: {Name(file)}");
        else if (TrackOf(Defaults(ecosystem, depName)) is { } account)
            (chosen, why) = (account, $"an account rule sets track: {Name(account)} (a coverage.yml rule with track: preview overrides it)");
        else if (IsPrerelease(ecosystem, currentVersion))
            (chosen, why) = (EDependencyTrack.Preview, $"{depName} is on a prerelease and follows prereleases");
        else if (DependenciesYml.PlatformSdks.Any(sdk => PlatformTrack(sdk) == EDependencyTrack.Preview && Companions(sdk).Any(glob => DependencyGlob.Matches(glob, depName))))
            (chosen, why) = (EDependencyTrack.Preview, $"{depName} is a companion of an SDK on the preview track");
        else
            (chosen, why) = (EDependencyTrack.Stable, $"{depName} follows stable releases (a coverage.yml rule `match: {depName}` with `track: preview` follows prereleases)");

        if (TrackOf(Enforced(ecosystem, depName)) is { } ceiling && ceiling < chosen)
            (chosen, why) = (ceiling, $"an enforced account rule sets track: {Name(ceiling)}");
        return (chosen == EDependencyTrack.Preview ? EDependencyTrack.Preview : EDependencyTrack.Stable, why);

        static string Name(EDependencyTrack track) => track.ToString().ToLowerInvariant();
    }

    /// <summary>prerelease-dependencies-PRD H5: the ecosystems whose versions are SemVer, where a <c>-</c> after the core always
    /// marks a prerelease. A Docker tag (<c>20-alpine</c>), a Maven qualifier (<c>33.0.0-jre</c>) or a Go pseudo-version uses it
    /// for other things.</summary>
    public static readonly IReadOnlySet<string> SemVerEcosystems = new HashSet<string>(StringComparer.Ordinal) { "nuget", "npm", "cargo", "composer" };

    /// <summary>The Renovate rule that matches the same versions (<c>renovate.ts</c>, H5) keeps this pattern.</summary>
    internal const string PrereleasePattern = @"^[\[(v^~=]*\d+(\.\d+)*-[0-9A-Za-z]";

    /// <summary>H1/H5: a SemVer version (or an exact range around one, <c>[0.4.0-beta]</c>) that is a prerelease.</summary>
    public static bool IsPrerelease(string ecosystem, string? version)
        => version is not null && SemVerEcosystems.Contains(ecosystem) && System.Text.RegularExpressions.Regex.IsMatch(version.Trim(), PrereleasePattern);

    /// <summary>The companion globs of an SDK: the file's <c>platforms.&lt;sdk&gt;.companions</c>, else the built-in list (#606 D2).</summary>
    public IReadOnlyList<string> Companions(string sdk)
        => File.Platforms.TryGetValue(sdk, out var settings) && settings.Companions is { } own ? own : DependencyCompanions.BuiltIn(sdk);

    /// <summary>
    /// The package globs Renovate must look up with prereleases (#606 D8): the file's and the account's preview-track rules
    /// on packages, and the companions of every SDK on the preview track. Renovate also looks up every package already on a
    /// prerelease with prereleases (H1), and then <see cref="StableGlobs"/> without them (H5, rule order in <c>renovate.ts</c>).
    /// The engine screens each candidate against <see cref="PackageTrackOf"/> (prerelease-dependencies-PRD D2), so an
    /// enforced rule narrows what this list widens. Renovate keeps one release per version bucket, so a glob here can displace
    /// a stable candidate: the list must stay as narrow as the tracks (SP2), never "everything".
    /// </summary>
    public IReadOnlyList<string> UnstableGlobs()
    {
        return File.Rules.Where(r => r.Track == EDependencyTrack.Preview && OnPackages(r.Ecosystem)).SelectMany(r => r.Match)
            .Concat(AccountRules.Where(r => !r.Enforced && r.Track == EDependencyTrack.Preview && OnPackages(r.Ecosystem)).Select(r => r.Match.Trim()))
            .Concat(DependenciesYml.PlatformSdks.Where(sdk => PlatformTrack(sdk) == EDependencyTrack.Preview).SelectMany(Companions))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// prerelease-dependencies-PRD H1/H5: the package globs Renovate looks up stable-only even when they are on a prerelease:
    /// the file's <c>track: stable</c> rules and every enforced account rule's stable track. A non-enforced account rule is a
    /// default a file rule replaces, so it is left to the engine's screen.
    /// </summary>
    public IReadOnlyList<string> StableGlobs()
        => File.Rules.Where(r => r.Track is EDependencyTrack.Stable or EDependencyTrack.Lts && OnPackages(r.Ecosystem)).SelectMany(r => r.Match)
            .Concat(AccountRules.Where(r => r.Enforced && r.Track is EDependencyTrack.Stable or EDependencyTrack.Lts && OnPackages(r.Ecosystem)).Select(r => r.Match.Trim()))
            .Distinct(StringComparer.Ordinal).ToList();

    /// <summary>prerelease-dependencies-PRD H3: the repository opted in to auto-merging this platform's majors
    /// (<c>platforms.&lt;sdk&gt;.autoMerge: major</c>; <paramref name="ecosystem"/> is <c>platform:&lt;sdk&gt;</c>).</summary>
    public bool PlatformAutoMergesMajor(string ecosystem)
        => ecosystem.StartsWith("platform:", StringComparison.Ordinal)
            && File.Platforms.TryGetValue(ecosystem["platform:".Length..], out var settings) && settings.AutoMergeMajor;

    private static bool OnPackages(string? ecosystem) => ecosystem is null || !ecosystem.Trim().StartsWith("platform:", StringComparison.Ordinal);

    private static EDependencyTrack? TrackOf(IEnumerable<DependencyRule> rules)
        => rules.OrderByDescending(r => DependencyGlob.Specificity(r.Match)).Select(r => r.Track).FirstOrDefault(t => t != EDependencyTrack.Inherit) is var t && t != EDependencyTrack.Inherit ? t : null;

    /// <summary>The narrower of two tracks (lts &lt; stable &lt; preview); a null ceiling leaves the track.</summary>
    internal static EDependencyTrack MinTrack(EDependencyTrack track, EDependencyTrack? ceiling)
        => ceiling is { } c && c < track ? c : track;

    /// <summary>The <c>migrate</c> workflow for an approved major of the dependency (R9), or null.</summary>
    public string? MigrateWorkflow(string depName)
        => File.Migrate.FirstOrDefault(m => m.Match.Any(glob => DependencyGlob.Matches(glob, depName)))?.OnMajor;

    private EDependencyThreshold RawThreshold(string ecosystem, string depName)
    {
        // prerelease-dependencies-PRD H3: `platforms.<sdk>.autoMerge: major` is the platform's threshold, whatever the globs
        // say; the enforced ceiling still applies on top (Threshold, Inclusion).
        if (PlatformAutoMergesMajor(ecosystem))
            return EDependencyThreshold.Major;
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

    // prerelease-dependencies-PRD D8: a platform's record is asked about as `platform:<sdk>` (ceiling, threshold) and as `<sdk>`
    // (track); a rule's match may name either, and then answers both questions.
    private IEnumerable<DependencyRule> AccountMatches(string ecosystem, string depName)
        => AccountRules.Where(r => (string.IsNullOrWhiteSpace(r.Ecosystem) || r.Ecosystem.Trim() == ecosystem)
            && PlatformAliases(ecosystem, depName).Any(name => DependencyGlob.Matches(r.Match.Trim(), name)));

    private static IEnumerable<string> PlatformAliases(string ecosystem, string depName)
    {
        yield return depName;
        if (!ecosystem.StartsWith("platform:", StringComparison.Ordinal)) yield break;
        var sdk = ecosystem["platform:".Length..];
        if (depName == ecosystem) yield return sdk;
        else if (depName == sdk) yield return ecosystem;
    }

    private IEnumerable<DependencyRule> Enforced(string ecosystem, string depName) => AccountMatches(ecosystem, depName).Where(r => r.Enforced);

    private IEnumerable<DependencyRule> Defaults(string ecosystem, string depName) => AccountMatches(ecosystem, depName).Where(r => !r.Enforced);

    internal static EDependencyThreshold Min(EDependencyThreshold a, EDependencyThreshold b) => a <= b ? a : b;
}
