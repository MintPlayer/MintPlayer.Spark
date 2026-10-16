using System.Globalization;
using System.Text.RegularExpressions;
using CodeCoverage.Git;
using CodeCoverage.LookupReferences;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeCoverage.Dependencies;

/// <summary>
/// The <c>dependencies:</c> section of <c>coverage.yml</c> (docs/code-coverage/dependency-updates-PRD.md §4.3, §13.3, R6,
/// R8, R9, D-S12e, gap B): the file half of the dependency updater's configuration.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Read from the repository's DEFAULT branch, never from the rolling branch or a pull request</b> (§13.11.3 #13), like
/// <c>MergeQueueYml</c>: a commit on <c>mintplayer/dependencies</c> cannot widen what the bot plans for itself.
/// </para>
/// <para>
/// <b>Errors are returned, never thrown.</b> A section with errors falls back to the built-in defaults (§4.3), keeping only
/// a valid <c>enabled: false</c>, so a typo can never switch a repository on or loosen a threshold; the errors are shown in
/// the pull request body. Unknown keys are ignored, so a newer file does not break an older server.
/// </para>
/// <code>
/// dependencies:
///   enabled: false                    # may only opt OUT (Q6); opting in is the account/repository setting
///   schedule: { frequency: weekly, weekday: monday, hour: 6 }   # overrides the settings' slot
///   cooldown: 7d                      # may only lengthen the settings' cooldown
///   autoMerge:                        # R6: the largest update planned and auto-merged without an approval
///     default: minor                  # never | patch | minor | major (built-in: minor)
///     "@angular/*": never             # globs over the dependency name; the most specific wins
///     maxCoverageDrop: 0.5            # R11, percentage points (built-in: 0)
///   ignore: ["Microsoft.CodeAnalysis*"]
///   rules:
///     - match: ["vitest", "@vitest/*"]
///       ecosystem: npm                # optional
///       allowMajor: true              # majors are planned without an approval (never auto-merged)
///       group: vitest
///       ignore: false
///       hold: "waiting for the Nx plugin"   # true or a reason: listed as held until removed
///   exclude: ["samples/**"]           # package files skipped
///   lookupAction: dependency-lookup.yml
///   applyAction: dependency-apply.yml
///   migrate:                          # R9: dispatched after apply, for approved over-threshold groups only
///     - match: "@angular/core"
///       onMajor: angular-migrate.yml
///   platforms:                        # R8
///     dotnet: { strategy: replace, keepPrevious: 0, lockPackageMajor: false, locations: [], exclude: [] }
///   lockFileMaintenance: false        # D-S12e, off by default
///   registries: ["npm.pkg.github.com"]   # gap B: extra lockfile hosts besides the public registries
/// </code>
/// </remarks>
public static partial class DependenciesYml
{
    public const string Source = "coverage.yml";
    public const string DefaultLookupAction = "dependency-lookup.yml";
    public const string DefaultApplyAction = "dependency-apply.yml";

    /// <summary>The SDKs a platform adapter exists for (R7).</summary>
    public static IReadOnlyList<string> PlatformSdks { get; } = ["dotnet", "node", "python", "java", "go"];

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Parses and validates <paramref name="yml"/>. The section is never null: absent, it is the defaults.</summary>
    public static DependenciesYmlResult Parse(string? yml)
    {
        if (string.IsNullOrWhiteSpace(yml))
            return new DependenciesYmlResult(DependenciesSection.Defaults(), []);

        FileDependencies? file;
        try
        {
            file = Deserializer.Deserialize<FileRoot?>(yml)?.Dependencies;
        }
        catch (Exception ex)
        {
            return new DependenciesYmlResult(DependenciesSection.Defaults(), [$"coverage.yml could not be read: {ex.Message}"]);
        }
        if (file is null)
            return new DependenciesYmlResult(DependenciesSection.Defaults(), []);

        var errors = new List<string>();
        var section = DependenciesSection.Defaults();
        section.Present = true;
        section.Enabled = file.Enabled ?? true;

        if (file.Schedule is { } schedule)
            section.Schedule = Schedule(schedule, errors);

        if (file.Cooldown is { } cooldown)
            section.MinimumCooldown = Days(cooldown, "dependencies.cooldown", errors);

        if (file.AutoMerge is { } autoMerge)
            AutoMerge(autoMerge, section, errors);

        foreach (var glob in Strings(file.Ignore, "dependencies.ignore", errors))
            section.Rules.Add(new DependencyFileRule([glob], null, null, null, true, null));

        foreach (var (rule, index) in (file.Rules ?? []).Select((r, i) => (r, i)))
        {
            var key = $"dependencies.rules[{index}]";
            if (rule is null)
            {
                errors.Add($"{key} is empty.");
                continue;
            }
            var match = Strings(rule.Match, $"{key}.match", errors);
            if (match.Count == 0)
            {
                errors.Add($"{key}.match must name at least one dependency glob.");
                continue;
            }
            var group = rule.Group?.Trim();
            if (group is { Length: > 200 }) errors.Add($"{key}.group is longer than 200 characters.");
            var hold = rule.Hold?.Trim() switch
            {
                null or "" or "false" => null,
                "true" => "held by coverage.yml",
                var reason => reason,
            };
            section.Rules.Add(new DependencyFileRule(match, Ecosystem(rule.Ecosystem, $"{key}.ecosystem", errors), rule.AllowMajor,
                string.IsNullOrEmpty(group) ? null : group, rule.Ignore ?? false, hold));
        }

        foreach (var path in Strings(file.Exclude, "dependencies.exclude", errors))
        {
            if (!IsPathGlob(path))
                errors.Add($"dependencies.exclude '{path}' is not a path glob.");
            else
                section.Exclude.Add(path);
        }

        section.LookupAction = Workflow(file.LookupAction, DefaultLookupAction, "dependencies.lookupAction", errors);
        section.ApplyAction = Workflow(file.ApplyAction, DefaultApplyAction, "dependencies.applyAction", errors);

        foreach (var (rule, index) in (file.Migrate ?? []).Select((r, i) => (r, i)))
        {
            var key = $"dependencies.migrate[{index}]";
            var match = Strings(rule?.Match, $"{key}.match", errors);
            var workflow = rule?.OnMajor?.Trim();
            if (match.Count == 0) errors.Add($"{key}.match must name at least one dependency glob.");
            if (string.IsNullOrEmpty(workflow)) errors.Add($"{key}.onMajor must name a workflow file.");
            else if (!WorkflowFileName().IsMatch(workflow)) errors.Add($"{key}.onMajor '{workflow}' must be a workflow file name under .github/workflows, e.g. angular-migrate.yml.");
            else if (match.Count > 0) section.Migrate.Add(new DependencyMigrateRule(match, workflow));
        }

        foreach (var (sdk, platform) in file.Platforms ?? [])
        {
            var key = $"dependencies.platforms.{sdk}";
            if (!PlatformSdks.Contains(sdk, StringComparer.Ordinal))
            {
                errors.Add($"{key}: '{sdk}' is not a platform; the platforms are {string.Join(", ", PlatformSdks)}.");
                continue;
            }
            var strategy = platform?.Strategy?.Trim() ?? "replace";
            if (strategy is not ("replace" or "add")) errors.Add($"{key}.strategy '{strategy}' is not replace or add.");
            var keepPrevious = platform?.KeepPrevious ?? 0;
            if (keepPrevious is < 0 or > 5) errors.Add($"{key}.keepPrevious must be between 0 and 5.");
            var locations = Strings(platform?.Locations, $"{key}.locations", errors);
            var exclude = Strings(platform?.Exclude, $"{key}.exclude", errors);
            foreach (var path in locations.Concat(exclude).Where(p => !IsPathGlob(p)))
                errors.Add($"{key}: '{path}' is not a path glob.");
            section.Platforms[sdk] = new DependencyPlatformSettings(strategy, Math.Clamp(keepPrevious, 0, 5), platform?.LockPackageMajor ?? false, locations, exclude);
        }

        section.LockFileMaintenance = file.LockFileMaintenance ?? false;

        foreach (var host in Strings(file.Registries, "dependencies.registries", errors))
        {
            var lower = host.ToLowerInvariant();
            if (!RegistryHost().IsMatch(lower))
                errors.Add($"dependencies.registries '{host}' is not a host name (e.g. npm.pkg.github.com).");
            else if (!section.Registries.Contains(lower))
                section.Registries.Add(lower);
        }

        if (errors.Count == 0)
            return new DependenciesYmlResult(section, []);

        // §4.3: a malformed section falls back to the defaults. Only an opt-out survives: it can only make the bot do less.
        var fallback = DependenciesSection.Defaults();
        fallback.Present = true;
        fallback.Enabled = file.Enabled ?? true;
        return new DependenciesYmlResult(fallback, errors);
    }

    private static DependencyScheduleOverride Schedule(FileSchedule file, List<string> errors)
    {
        EDependencyScheduleFrequency? frequency = file.Frequency?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "daily" => EDependencyScheduleFrequency.Daily,
            "weekly" => EDependencyScheduleFrequency.Weekly,
            "monthly" => EDependencyScheduleFrequency.Monthly,
            var other => Error<EDependencyScheduleFrequency?>(errors, $"dependencies.schedule.frequency '{other}' is not daily, weekly or monthly.", null),
        };
        EDependencyWeekday? weekday = null;
        if (file.Weekday?.Trim() is { Length: > 0 } day)
        {
            if (Enum.TryParse<EDependencyWeekday>(day, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(day, out _))
                weekday = parsed;
            else
                errors.Add($"dependencies.schedule.weekday '{day}' is not a weekday (monday to sunday).");
        }
        int? hour = null;
        if (file.Hour is { } h)
        {
            if (h is < 0 or > 23) errors.Add("dependencies.schedule.hour must be between 0 and 23.");
            else hour = h;
        }
        return new DependencyScheduleOverride(frequency, weekday, hour);
    }

    private static void AutoMerge(Dictionary<string, string?> map, DependenciesSection section, List<string> errors)
    {
        foreach (var (rawKey, rawValue) in map)
        {
            var key = rawKey.Trim();
            var value = rawValue?.Trim() ?? string.Empty;
            if (key == "maxCoverageDrop")
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var drop) && drop is >= 0 and <= 100)
                    section.MaxCoverageDrop = drop;
                else
                    errors.Add($"dependencies.autoMerge.maxCoverageDrop '{value}' must be a number of percentage points between 0 and 100.");
                continue;
            }
            if (!TryThreshold(value, out var threshold))
            {
                errors.Add($"dependencies.autoMerge.{key} '{value}' is not never, patch, minor or major.");
                continue;
            }
            if (key == "default")
                section.DefaultThreshold = threshold;
            else if (key.Length == 0)
                errors.Add("dependencies.autoMerge holds an empty glob.");
            else
                section.AutoMerge.Add(new DependencyThresholdGlob(key, threshold));
        }
    }

    public static bool TryThreshold(string? value, out EDependencyThreshold threshold)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "never": threshold = EDependencyThreshold.Never; return true;
            case "patch": threshold = EDependencyThreshold.Patch; return true;
            case "minor": threshold = EDependencyThreshold.Minor; return true;
            case "major": threshold = EDependencyThreshold.Major; return true;
            default: threshold = default; return false;
        }
    }

    private static string? Ecosystem(string? value, string key, List<string> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (!EcosystemName().IsMatch(trimmed))
        {
            errors.Add($"{key} '{trimmed}' is not an ecosystem name (npm, nuget, github-actions, platform:dotnet, ...).");
            return null;
        }
        return trimmed;
    }

    private static string Workflow(string? value, string fallback, string key, List<string> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return fallback;
        if (WorkflowFileName().IsMatch(trimmed)) return trimmed;
        errors.Add($"{key} '{trimmed}' must be a workflow file name under .github/workflows, e.g. {fallback}.");
        return fallback;
    }

    /// <summary>A scalar or a list of scalars, trimmed; empty entries are errors.</summary>
    private static List<string> Strings(object? value, string key, List<string> errors)
    {
        var result = new List<string>();
        switch (value)
        {
            case null:
                break;
            case string single:
                Add(single);
                break;
            case IEnumerable<object?> list:
                foreach (var item in list)
                {
                    if (item is string text) Add(text);
                    else errors.Add($"{key} holds a value that is not text.");
                }
                break;
            default:
                errors.Add($"{key} must be text or a list of text.");
                break;
        }
        return result;

        void Add(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0) errors.Add($"{key} holds an empty value.");
            else if (!result.Contains(trimmed, StringComparer.Ordinal)) result.Add(trimmed);
        }
    }

    /// <summary>A duration in whole days: <c>0</c>, <c>3d</c>; at most 90 days.</summary>
    private static TimeSpan? Days(string value, string key, List<string> errors)
    {
        var match = DayDuration().Match(value.Trim());
        if (!match.Success || !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days > 90)
            return Error<TimeSpan?>(errors, $"{key} '{value}' is not a number of days such as 0, 3d or 7d (at most 90d).", null);
        return TimeSpan.FromDays(days);
    }

    private static bool IsPathGlob(string glob)
    {
        if (glob.StartsWith('/') || glob.Split('/').Any(s => s == ".."))
            return false;
        try
        {
            _ = new PathGlob(glob);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static T Error<T>(List<string> errors, string message, T fallback)
    {
        errors.Add(message);
        return fallback;
    }

    [GeneratedRegex(@"^(?<n>[0-9]{1,3})d?$", RegexOptions.CultureInvariant)]
    private static partial Regex DayDuration();

    /// <summary>The shape the forge accepts for a workflow file, as <c>MergeQueueYml</c> holds it.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.ya?ml$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowFileName();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9:._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EcosystemName();

    [GeneratedRegex(@"^(?=.{1,253}(:[0-9]{1,5})?$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+(:[0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryHost();

    private sealed class FileRoot
    {
        public FileDependencies? Dependencies { get; set; }
    }

    private sealed class FileDependencies
    {
        public bool? Enabled { get; set; }
        public FileSchedule? Schedule { get; set; }
        public string? Cooldown { get; set; }
        public Dictionary<string, string?>? AutoMerge { get; set; }
        public object? Ignore { get; set; }
        public List<FileRule?>? Rules { get; set; }
        public object? Exclude { get; set; }
        public string? LookupAction { get; set; }
        public string? ApplyAction { get; set; }
        public List<FileMigrate?>? Migrate { get; set; }
        public Dictionary<string, FilePlatform?>? Platforms { get; set; }
        public bool? LockFileMaintenance { get; set; }
        public object? Registries { get; set; }
    }

    private sealed class FileSchedule
    {
        public string? Frequency { get; set; }
        public string? Weekday { get; set; }
        public int? Hour { get; set; }
    }

    private sealed class FileRule
    {
        public object? Match { get; set; }
        public string? Ecosystem { get; set; }
        public bool? AllowMajor { get; set; }
        public string? Group { get; set; }
        public bool? Ignore { get; set; }
        public string? Hold { get; set; }
    }

    private sealed class FileMigrate
    {
        public object? Match { get; set; }
        public string? OnMajor { get; set; }
    }

    private sealed class FilePlatform
    {
        public string? Strategy { get; set; }
        public int? KeepPrevious { get; set; }
        public bool? LockPackageMajor { get; set; }
        public object? Locations { get; set; }
        public object? Exclude { get; set; }
    }
}

/// <summary>The parse: the section (the defaults when absent or broken) and what is wrong with it.</summary>
public sealed record DependenciesYmlResult(DependenciesSection Section, IReadOnlyList<string> Errors);

/// <summary>A schedule set in the file; each part overrides the settings' value when present.</summary>
public sealed record DependencyScheduleOverride(EDependencyScheduleFrequency? Frequency, EDependencyWeekday? Weekday, int? Hour);

/// <summary>An <c>autoMerge</c> glob and its threshold (R6).</summary>
public sealed record DependencyThresholdGlob(string Glob, EDependencyThreshold Threshold);

/// <summary>A <c>rules</c> entry, or an <c>ignore</c> glob as a rule.</summary>
/// <param name="Hold">A reason when the rule holds the matching dependencies; null otherwise.</param>
public sealed record DependencyFileRule(IReadOnlyList<string> Match, string? Ecosystem, bool? AllowMajor, string? Group, bool Ignore, string? Hold);

/// <summary>A <c>migrate</c> entry (R9): the workflow dispatched after an approved major of a matching dependency.</summary>
public sealed record DependencyMigrateRule(IReadOnlyList<string> Match, string OnMajor);

/// <summary><c>platforms.&lt;sdk&gt;</c> (R8).</summary>
public sealed record DependencyPlatformSettings(string Strategy, int KeepPrevious, bool LockPackageMajor, IReadOnlyList<string> Locations, IReadOnlyList<string> Exclude)
{
    public static DependencyPlatformSettings Default { get; } = new("replace", 0, false, [], []);
}

/// <summary>The validated file half of the dependency updater's configuration.</summary>
public sealed class DependenciesSection
{
    /// <summary>The section is in the file (even when broken).</summary>
    public bool Present { get; set; }

    /// <summary>False only when the file opts out (Q6); the settings decide whether the repository is in at all.</summary>
    public bool Enabled { get; set; } = true;

    public DependencyScheduleOverride? Schedule { get; set; }

    /// <summary>The file's cooldown; it may only lengthen the settings' value.</summary>
    public TimeSpan? MinimumCooldown { get; set; }

    /// <summary><c>autoMerge.default</c>; built-in <see cref="EDependencyThreshold.Minor"/>.</summary>
    public EDependencyThreshold DefaultThreshold { get; set; } = EDependencyThreshold.Minor;

    /// <summary>The file's <c>autoMerge</c> globs, in file order; they win over <see cref="BuiltInThresholds"/>.</summary>
    public List<DependencyThresholdGlob> AutoMerge { get; set; } = [];

    /// <summary>R11: the coverage drop (percentage points) auto-merge tolerates; built-in 0.</summary>
    public double MaxCoverageDrop { get; set; }

    public List<DependencyFileRule> Rules { get; set; } = [];

    /// <summary>Package-file path globs the updater skips.</summary>
    public List<string> Exclude { get; set; } = [];

    public string LookupAction { get; set; } = DependenciesYml.DefaultLookupAction;
    public string ApplyAction { get; set; } = DependenciesYml.DefaultApplyAction;
    public List<DependencyMigrateRule> Migrate { get; set; } = [];
    public Dictionary<string, DependencyPlatformSettings> Platforms { get; set; } = new(StringComparer.Ordinal);
    public bool LockFileMaintenance { get; set; }

    /// <summary>Gap B: lockfile hosts allowed besides the public registries, lower-case.</summary>
    public List<string> Registries { get; set; } = [];

    /// <summary>R6's built-in thresholds, below every glob of the file: Angular moves only by approval (it moves with
    /// migrations, R9), Actions majors are over the threshold (R15).</summary>
    public static IReadOnlyList<DependencyThresholdGlob> BuiltInThresholds { get; } =
    [
        new("@angular/*", EDependencyThreshold.Never),
        new("actions/*", EDependencyThreshold.Minor),
    ];

    public static DependenciesSection Defaults() => new();

    public DependencyPlatformSettings Platform(string sdk) => Platforms.GetValueOrDefault(sdk) ?? DependencyPlatformSettings.Default;
}
