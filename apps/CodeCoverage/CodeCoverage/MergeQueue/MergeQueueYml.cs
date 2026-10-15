using System.Globalization;
using System.Text.RegularExpressions;
using CodeCoverage.Entities;
using CodeCoverage.Git.MergeQueue;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// The <c>mergeQueue:</c> section of <c>coverage.yml</c> (merge-queue PRD §5.2): the file behaviour of the queue (O15).
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Read from the repository's DEFAULT branch, never from the pull request's base (O16).</b> Otherwise a pull request
/// into branch A could run under a configuration somebody widened on A. The caller passes the default branch's file.
/// </para>
/// <para>
/// <b>Errors are returned, never thrown</b>, like <c>CoverageYml</c>'s gate (<c>PublishFeedbackRecipient</c>'s
/// <c>ymlError</c>): a broken section refuses the enqueue and the queue's check run says why. Unknown keys are ignored,
/// so a newer file does not break an older server; every documented key is validated.
/// </para>
/// <para>
/// Values that reach the git writer (branches, <c>regeneratedPaths</c>, <c>autoResolve</c>) are held to the writer's own
/// rules, <see cref="MergeQueueWireRules"/> (§0 D57): whatever parses here, the writer accepts.
/// </para>
/// <code>
/// mergeQueue:
///   enabled: true
///   branches: [master]               # default: the default branch
///   mergeMethod: squash              # v1: squash only
///   enqueue: { label: merge-queue }
///   postRebaseActionName: regenerate.yml
///   postRebaseTimeout: 20m           # s, m or h
///   postRebaseWhen: auto-resolved    # auto-resolved | always | never
///   regeneratedPaths: ["**/App_Data/modelHashes.json"]
///   autoResolve:                     # first match wins; then autoResolveDefaults, then keepBothDefaults
///     - path: "**/App_Data/modelHashes.json"
///       strategy: take-base          # take-base | keep-both | line-set | evict (§10.3); keys: [..] scope take-base (O2)
///     - path: "**/security.json"
///       strategy: keep-both
///       validate: [json-no-duplicate-keys]
///       regenerate: true             # default: take-base always; others when a workflow is set (O36)
///       action: regenerate.yml       # default: postRebaseActionName (O36)
///   autoResolveDefaults: [npm]       # opt-in take-base presets: spark, npm, pnpm, yarn, nuget
///   keepBothDefaults: { xmlProjects: false }   # default all on (O35 #1, O36); false turns every group off
///   checks: { retryFlaky: 1, timeout: 60m }
/// </code>
/// </remarks>
public static partial class MergeQueueYml
{
    public const string Source = "coverage.yml";
    public const string DefaultSource = "default";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses and validates <paramref name="yml"/>. <see cref="MergeQueueYmlResult.Section"/> is null when the file or the
    /// section is absent; <see cref="MergeQueueYmlResult.Errors"/> is non-empty when the section is unusable.
    /// </summary>
    public static MergeQueueYmlResult Parse(string? yml, string defaultBranch)
    {
        if (string.IsNullOrWhiteSpace(yml))
            return new MergeQueueYmlResult(null, []);

        FileMergeQueue? file;
        try
        {
            file = Deserializer.Deserialize<FileRoot?>(yml)?.MergeQueue;
        }
        catch (Exception ex)
        {
            return new MergeQueueYmlResult(null, [$"coverage.yml could not be read: {ex.Message}"]);
        }
        if (file is null)
            return new MergeQueueYmlResult(null, []);

        var errors = new List<string>();
        var values = new List<MergeQueueConfigValue>();
        void Value(string key, string value, bool fromFile) => values.Add(new MergeQueueConfigValue { Key = key, Value = value, Source = fromFile ? Source : DefaultSource });

        var section = new MergeQueueSection { Enabled = file.Enabled ?? false };
        Value("enabled", section.Enabled ? "true" : "false", file.Enabled is not null);

        var branches = (file.Branches ?? []).Select(b => b?.Trim() ?? string.Empty).ToList();
        if (branches.Any(b => b.Length == 0)) errors.Add("mergeQueue.branches holds an empty name.");
        section.Branches = file.Branches is null ? [defaultBranch] : [.. branches.Where(b => b.Length > 0).Distinct(StringComparer.Ordinal)];
        if (section.Branches.Count == 0) errors.Add("mergeQueue.branches is empty, so no pull request can be queued.");
        // §0 D57: every value below reaches the git writer, so it is held to the writer's own rules here, where the error
        // can be read, instead of being refused on every request of every pull request.
        foreach (var branch in section.Branches.Where(b => !MergeQueueWireRules.IsQueueableBase(b)))
            errors.Add(MergeQueueWireRules.IsBranch(branch)
                ? $"mergeQueue.branches '{branch}' is too long: its queue branch mq/{branch}/pr-<number> must stay within {MergeQueueWireRules.MaxBranchLength} characters."
                : $"mergeQueue.branches '{branch}' is not a branch name the queue's git writer accepts: {MergeQueueWireRules.BranchRule}.");
        Value("branches", string.Join(", ", section.Branches), file.Branches is not null);

        if (file.MergeMethod is not null && file.MergeMethod != "squash")
            errors.Add($"mergeQueue.mergeMethod '{file.MergeMethod}' is not supported; v1 merges with squash only.");
        Value("mergeMethod", "squash", file.MergeMethod is not null);

        var label = file.Enqueue?.Label?.Trim();
        if (label is not null && label.Length is 0 or > 50) errors.Add("mergeQueue.enqueue.label must be 1 to 50 characters.");
        section.Label = string.IsNullOrEmpty(label) ? "merge-queue" : label;
        Value("enqueue.label", section.Label, label is not null);

        section.PostRebaseActionName = file.PostRebaseActionName?.Trim() is { Length: > 0 } action ? action : null;
        if (section.PostRebaseActionName is { } name && !WorkflowFileName().IsMatch(name))
            errors.Add($"mergeQueue.postRebaseActionName '{name}' must be a workflow file name under .github/workflows, e.g. regenerate.yml.");
        Value("postRebaseActionName", section.PostRebaseActionName ?? "(none)", section.PostRebaseActionName is not null);

        section.PostRebaseTimeoutMinutes = Minutes(file.PostRebaseTimeout, 20, 1, 360, "mergeQueue.postRebaseTimeout", errors);
        Value("postRebaseTimeout", $"{section.PostRebaseTimeoutMinutes}m", file.PostRebaseTimeout is not null);

        section.PostRebaseWhen = file.PostRebaseWhen switch
        {
            null or "auto-resolved" => EPostRebaseWhen.AutoResolved,
            "always" => EPostRebaseWhen.Always,
            "never" => EPostRebaseWhen.Never,
            var other => Error(errors, $"mergeQueue.postRebaseWhen '{other}' is not auto-resolved, always or never.", EPostRebaseWhen.AutoResolved),
        };
        Value("postRebaseWhen", section.PostRebaseWhen switch
        {
            EPostRebaseWhen.Always => "always",
            EPostRebaseWhen.Never => "never",
            _ => "auto-resolved",
        }, file.PostRebaseWhen is not null);

        section.RegeneratedPaths = [.. (file.RegeneratedPaths ?? []).Select(p => p?.Trim() ?? string.Empty)];
        if (section.RegeneratedPaths.Any(p => p.Length == 0)) errors.Add("mergeQueue.regeneratedPaths holds an empty pattern.");
        section.RegeneratedPaths.RemoveAll(p => p.Length == 0);
        if (section.RegeneratedPaths.Count > MergeQueueWireRules.MaxGlobs)
            errors.Add($"mergeQueue.regeneratedPaths holds {section.RegeneratedPaths.Count} patterns; the queue's git writer takes at most {MergeQueueWireRules.MaxGlobs}.");
        foreach (var path in section.RegeneratedPaths.Where(p => !MergeQueueWireRules.IsGlob(p)))
            errors.Add($"mergeQueue.regeneratedPaths '{path}' is not a path glob the queue's git writer accepts: {MergeQueueWireRules.GlobRule}.");
        Value("regeneratedPaths", section.RegeneratedPaths.Count == 0 ? "(none)" : string.Join(", ", section.RegeneratedPaths), file.RegeneratedPaths is not null);

        // §10.3, O35, O36: the explicit rules first (first match wins), then the opt-in presets, then the keep-both defaults.
        ReadExplicitRules(file.AutoResolve, section, errors);

        foreach (var presetName in (file.AutoResolveDefaults ?? []).Select(n => n?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal))
        {
            if (!AutoResolvePresets.Presets.TryGetValue(presetName, out var preset))
            {
                errors.Add($"mergeQueue.autoResolveDefaults '{presetName}' is not a preset; the presets are {string.Join(", ", AutoResolvePresets.Presets.Keys)}.");
                continue;
            }
            section.AutoResolveDefaults.Add(presetName);
            section.AutoResolve.AddRange(preset.Select(p => FromPreset(p, $"preset {presetName}")));
        }
        Value("autoResolveDefaults", section.AutoResolveDefaults.Count == 0 ? "(none)" : string.Join(", ", section.AutoResolveDefaults), file.AutoResolveDefaults is not null);

        var keepBoth = KeepBothGroups(file.KeepBothDefaults, errors);
        foreach (var group in AutoResolvePresets.KeepBothDefaultOrder.Where(keepBoth.Contains))
        {
            section.KeepBothDefaults.Add(group);
            section.AutoResolve.AddRange(AutoResolvePresets.KeepBothDefaults[group].Select(p => FromPreset(p, DefaultSource)));
        }
        Value("keepBothDefaults", section.KeepBothDefaults.Count == 0 ? "(none)" : string.Join(", ", section.KeepBothDefaults), file.KeepBothDefaults is not null);

        if (section.AutoResolve.Count > MergeQueueWireRules.MaxGlobs)
            errors.Add($"mergeQueue.autoResolve holds {section.AutoResolve.Count} entries with its presets and defaults; the queue's git writer takes at most {MergeQueueWireRules.MaxGlobs}.");

        // O36: whether each rule regenerates, and with which workflow. take-base always does (an error when it cannot);
        // keep-both and line-set do when a workflow is set for them and the queue runs workflows at all.
        var never = section.PostRebaseWhen == EPostRebaseWhen.Never;
        bool takeBaseWithoutWorkflow = false, takeBaseNever = false;
        foreach (var rule in section.AutoResolve)
        {
            var workflow = rule.Action ?? section.PostRebaseActionName;
            switch (rule.Strategy)
            {
                case EMergeQueueResolveStrategy.Evict:
                    rule.Regenerate = false;
                    break;
                case EMergeQueueResolveStrategy.TakeBase:
                    rule.Regenerate = true;
                    takeBaseWithoutWorkflow |= workflow is null;
                    takeBaseNever |= never;
                    break;
                default:
                    if (rule.Regenerate == true && (workflow is null || never))
                        errors.Add($"mergeQueue.autoResolve '{rule.Path}' asks for regenerate, but no workflow runs for it: set its action or mergeQueue.postRebaseActionName, and a postRebaseWhen other than never.");
                    rule.Regenerate ??= workflow is not null && !never;
                    break;
            }
        }
        // §5.2: take-base leaves the generated files stale on purpose, so the regenerate step is not optional.
        if (takeBaseWithoutWorkflow)
            errors.Add("mergeQueue.autoResolve takes the base side of generated files, which only the regenerate workflow can fix: set mergeQueue.postRebaseActionName (or the rule's action).");
        if (takeBaseNever)
            errors.Add("mergeQueue.postRebaseWhen 'never' cannot be combined with a take-base autoResolve rule: a taken base side is never regenerated.");

        // Each rule with its source (O15): coverage.yml, a preset, or the default.
        if (!section.AutoResolve.Any(r => r.Source == Source))
            Value("autoResolve", "(none)", file.AutoResolve is not null);
        foreach (var rule in section.AutoResolve)
            values.Add(new MergeQueueConfigValue { Key = "autoResolve", Value = Describe(rule), Source = rule.Source ?? DefaultSource });

        // O36: a regenerating rule's paths are paths its workflow may write, beside the explicit regeneratedPaths.
        foreach (var path in section.AutoResolve.Where(r => r.Regenerate == true).Select(r => r.Path))
            if (!section.RegeneratedPaths.Contains(path, StringComparer.Ordinal))
                section.RegeneratedPaths.Add(path);
        if (section.RegeneratedPaths.Count > MergeQueueWireRules.MaxGlobs)
            errors.Add($"mergeQueue.regeneratedPaths holds {section.RegeneratedPaths.Count} patterns with the regenerating autoResolve rules; the queue's git writer takes at most {MergeQueueWireRules.MaxGlobs}.");
        if (values.FindIndex(v => v.Key == "regeneratedPaths") is >= 0 and var shown)
            values[shown].Value = section.RegeneratedPaths.Count == 0 ? "(none)" : string.Join(", ", section.RegeneratedPaths);

        if (section.PostRebaseWhen == EPostRebaseWhen.Always && section.PostRebaseActionName is null && !section.AutoResolve.Any(r => r.Action is not null))
            errors.Add("mergeQueue.postRebaseWhen 'always' needs mergeQueue.postRebaseActionName.");
        // A workflow runs only when a rule regenerates (whose paths were just added) or under 'always'; under
        // 'auto-resolved' with no regenerating rule nothing is ever dispatched, so an empty list refuses nothing.
        var anyWorkflow = section.PostRebaseActionName is not null || section.AutoResolve.Any(r => r.Action is not null);
        var dispatches = section.PostRebaseWhen == EPostRebaseWhen.Always || section.AutoResolve.Any(r => r.Regenerate == true);
        if (anyWorkflow && dispatches && !never && section.RegeneratedPaths.Count == 0)
            errors.Add("mergeQueue.regeneratedPaths is empty, so every regenerate patch would be refused.");
        if (section.Workflows().Count > MergeQueueWireRules.MaxRegenerateWorkflows)
            errors.Add($"mergeQueue.autoResolve names {section.Workflows().Count} regenerate workflows; the queue runs at most {MergeQueueWireRules.MaxRegenerateWorkflows}.");

        section.RetryFlaky = file.Checks?.RetryFlaky ?? 1;
        if (section.RetryFlaky is < 0 or > 3)
        {
            errors.Add("mergeQueue.checks.retryFlaky must be 0 to 3.");
            section.RetryFlaky = 1;
        }
        Value("checks.retryFlaky", section.RetryFlaky.ToString(CultureInfo.InvariantCulture), file.Checks?.RetryFlaky is not null);

        section.ChecksTimeoutMinutes = Minutes(file.Checks?.Timeout, 60, 5, 720, "mergeQueue.checks.timeout", errors);
        Value("checks.timeout", $"{section.ChecksTimeoutMinutes}m", file.Checks?.Timeout is not null);

        section.Values = values;
        return new MergeQueueYmlResult(section, errors);
    }

    /// <summary>The <c>autoResolve</c> entries as written, each held to the git writer's rules (§0 D57, §10.3).</summary>
    private static void ReadExplicitRules(List<FileAutoResolve?>? rules, MergeQueueSection section, List<string> errors)
    {
        if (rules is { Count: > MergeQueueWireRules.MaxGlobs } tooMany)
            errors.Add($"mergeQueue.autoResolve holds {tooMany.Count} entries; the queue's git writer takes at most {MergeQueueWireRules.MaxGlobs}.");
        foreach (var rule in rules ?? [])
        {
            var path = rule?.Path?.Trim();
            if (string.IsNullOrEmpty(path))
            {
                errors.Add("mergeQueue.autoResolve has an entry without a path.");
                continue;
            }
            if (!MergeQueueWireRules.IsGlob(path))
            {
                errors.Add($"mergeQueue.autoResolve '{path}' is not a path glob the queue's git writer accepts: {MergeQueueWireRules.GlobRule}.");
                continue;
            }
            var strategyName = rule!.Strategy?.Trim() ?? MergeQueueWireRules.TakeBase;
            if (!MergeQueueWireRules.Strategies.Contains(strategyName))
            {
                // O1: json-union is dropped; anything a person writes is merged by the bot only through these strategies.
                errors.Add($"mergeQueue.autoResolve '{path}': strategy '{rule.Strategy}' is not supported; the strategies are {string.Join(", ", MergeQueueWireRules.Strategies.Order(StringComparer.Ordinal))} (a conflict anywhere else evicts).");
                continue;
            }
            var strategy = StrategyOf(strategyName);
            List<string> keys = [.. (rule.Keys ?? []).Select(k => k?.Trim() ?? string.Empty).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)];
            if (keys.Count > 0 && strategy != EMergeQueueResolveStrategy.TakeBase)
                errors.Add($"mergeQueue.autoResolve '{path}': keys only scope take-base.");
            else if (keys.Count > MergeQueueWireRules.MaxGlobs)
                errors.Add($"mergeQueue.autoResolve '{path}' lists {keys.Count} keys; the queue's git writer takes at most {MergeQueueWireRules.MaxGlobs}.");
            else if (keys.Any(k => !MergeQueueWireRules.IsKey(k)))
                errors.Add($"mergeQueue.autoResolve '{path}' has a key the queue's git writer does not accept: {MergeQueueWireRules.KeyRule}.");

            List<string> validate = [.. (rule.Validate ?? []).Select(v => v?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal)];
            if (validate.Count > MergeQueueWireRules.MaxValidators)
                errors.Add($"mergeQueue.autoResolve '{path}' lists {validate.Count} validators; the queue's git writer takes at most {MergeQueueWireRules.MaxValidators}.");
            foreach (var unknown in validate.Where(v => !MergeQueueWireRules.Validators.Contains(v)))
                errors.Add($"mergeQueue.autoResolve '{path}': validator '{unknown}' is not one of {string.Join(", ", MergeQueueWireRules.Validators.Order(StringComparer.Ordinal))}.");

            if (strategy == EMergeQueueResolveStrategy.TakeBase && rule.Regenerate == false)
                errors.Add($"mergeQueue.autoResolve '{path}': take-base with regenerate: false would merge a stale generated file; take-base always regenerates.");
            if (strategy == EMergeQueueResolveStrategy.Evict && (validate.Count > 0 || rule.Regenerate is not null || rule.Action is not null))
                errors.Add($"mergeQueue.autoResolve '{path}': evict resolves nothing, so it takes no validate, regenerate or action.");

            var action = rule.Action?.Trim();
            if (action is not null && !MergeQueueWireRules.IsWorkflowFileName(action))
                errors.Add($"mergeQueue.autoResolve '{path}': action '{action}' must be {MergeQueueWireRules.WorkflowFileRule}.");

            section.AutoResolve.Add(new MergeQueueAutoResolveRule
            {
                Path = path,
                Keys = strategy == EMergeQueueResolveStrategy.TakeBase ? keys : [],
                Strategy = strategy,
                Validate = validate,
                Regenerate = rule.Regenerate,
                Action = string.IsNullOrEmpty(action) ? null : action,
                Source = Source,
            });
        }
    }

    private static EMergeQueueResolveStrategy StrategyOf(string name) => name switch
    {
        MergeQueueWireRules.KeepBoth => EMergeQueueResolveStrategy.KeepBoth,
        MergeQueueWireRules.LineSet => EMergeQueueResolveStrategy.LineSet,
        MergeQueueWireRules.Evict => EMergeQueueResolveStrategy.Evict,
        _ => EMergeQueueResolveStrategy.TakeBase,
    };

    private static MergeQueueAutoResolveRule FromPreset(AutoResolvePresetRule preset, string source) => new()
    {
        Path = preset.Path,
        Strategy = StrategyOf(preset.Strategy),
        Validate = [.. preset.Validate],
        Regenerate = preset.Regenerate,
        Source = source,
    };

    /// <summary>
    /// <c>keepBothDefaults</c> (O35 #1, O36 #1): absent or <c>true</c> turns every group on, <c>false</c> every group off,
    /// and a map turns single groups off (<c>{ json: false }</c>); a group the map does not name stays on.
    /// </summary>
    private static IReadOnlySet<string> KeepBothGroups(object? value, List<string> errors)
    {
        var all = AutoResolvePresets.KeepBothDefaultOrder.ToHashSet(StringComparer.Ordinal);
        switch (value)
        {
            case null:
                return all;
            case string text when bool.TryParse(text.Trim(), out var on):
                return on ? all : new HashSet<string>(StringComparer.Ordinal);
            case IDictionary<object, object> map:
                var groups = new HashSet<string>(all, StringComparer.Ordinal);
                foreach (var (key, raw) in map)
                {
                    var name = key?.ToString() ?? string.Empty;
                    if (!all.Contains(name))
                    {
                        errors.Add($"mergeQueue.keepBothDefaults '{name}' is not a default; the defaults are {string.Join(", ", AutoResolvePresets.KeepBothDefaultOrder)}.");
                        continue;
                    }
                    if (raw is not string flag || !bool.TryParse(flag.Trim(), out var enabled))
                    {
                        errors.Add($"mergeQueue.keepBothDefaults.{name} must be true or false.");
                        continue;
                    }
                    if (!enabled)
                        groups.Remove(name);
                }
                return groups;
            default:
                errors.Add("mergeQueue.keepBothDefaults must be true, false, or a map of json, ignoreFiles, changelog and xmlProjects to true or false.");
                return all;
        }
    }

    /// <summary>A rule as the check run shows it: path, strategy, validators, whether it regenerates and where it came from.</summary>
    internal static string Describe(MergeQueueAutoResolveRule rule)
    {
        var parts = new List<string> { MergeQueueAutoResolveRule.WireName(rule.Strategy) };
        if (rule.Keys.Count > 0) parts.Add($"keys {string.Join("/", rule.Keys)}");
        if (rule.Validate.Count > 0) parts.Add($"validate {string.Join("/", rule.Validate)}");
        if (rule.Regenerate == true) parts.Add(rule.Action is { } action ? $"regenerate with {action}" : "regenerate");
        return $"{rule.Path} ({string.Join(", ", parts)})";
    }

    private static T Error<T>(List<string> errors, string message, T fallback)
    {
        errors.Add(message);
        return fallback;
    }

    /// <summary>A duration such as <c>90s</c>, <c>20m</c> or <c>1h</c>, in whole minutes (rounded up), bounded.</summary>
    private static int Minutes(string? value, int fallback, int min, int max, string key, List<string> errors)
    {
        if (value is null) return fallback;
        var match = Duration().Match(value.Trim());
        if (!match.Success || !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return Error(errors, $"{key} '{value}' is not a duration such as 90s, 20m or 1h.", fallback);
        var minutes = match.Groups["u"].Value switch
        {
            "s" => (n + 59) / 60,
            "h" => n * 60,
            _ => n,
        };
        return minutes < min || minutes > max
            ? Error(errors, $"{key} must be between {min} and {max} minutes.", fallback)
            : minutes;
    }

    [GeneratedRegex(@"^(?<n>[0-9]{1,5})(?<u>[smh]?)$", RegexOptions.CultureInvariant)]
    private static partial Regex Duration();

    /// <summary>The same shape the forge accepts for a workflow file (<c>GitHubForgeMergeQueue.WorkflowFileName</c>).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.ya?ml$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowFileName();

    private sealed class FileRoot
    {
        public FileMergeQueue? MergeQueue { get; set; }
    }

    private sealed class FileMergeQueue
    {
        public bool? Enabled { get; set; }
        public List<string>? Branches { get; set; }
        public string? MergeMethod { get; set; }
        public FileEnqueue? Enqueue { get; set; }
        public string? PostRebaseActionName { get; set; }
        public string? PostRebaseTimeout { get; set; }
        public string? PostRebaseWhen { get; set; }
        public List<string>? RegeneratedPaths { get; set; }
        public List<FileAutoResolve?>? AutoResolve { get; set; }
        public List<string?>? AutoResolveDefaults { get; set; }

        // true, false, or a map of group to true/false (a scalar or a mapping). No doc comment: the Spark generator reads them.
        public object? KeepBothDefaults { get; set; }

        public FileChecks? Checks { get; set; }
    }

    private sealed class FileEnqueue
    {
        public string? Label { get; set; }
    }

    private sealed class FileAutoResolve
    {
        public string? Path { get; set; }
        public string? Strategy { get; set; }
        public List<string>? Keys { get; set; }
        public List<string?>? Validate { get; set; }
        public bool? Regenerate { get; set; }
        public string? Action { get; set; }
    }

    private sealed class FileChecks
    {
        public int? RetryFlaky { get; set; }
        public string? Timeout { get; set; }
    }
}

/// <summary>The parse: the section (null when absent or unreadable) and what is wrong with it.</summary>
public sealed record MergeQueueYmlResult(MergeQueueSection? Section, IReadOnlyList<string> Errors)
{
    public bool IsUsable => Section is { Enabled: true } && Errors.Count == 0;
}

/// <summary>The validated file half of the configuration, each value's source in <see cref="Values"/>.</summary>
public sealed class MergeQueueSection
{
    public bool Enabled { get; set; }
    public List<string> Branches { get; set; } = [];
    public string Label { get; set; } = "merge-queue";
    public string? PostRebaseActionName { get; set; }
    public int PostRebaseTimeoutMinutes { get; set; } = 20;
    public EPostRebaseWhen PostRebaseWhen { get; set; }
    public List<string> RegeneratedPaths { get; set; } = [];
    /// <summary>The explicit rules, then the presets, then the keep-both defaults; first match wins (§10.3, O36).</summary>
    public List<MergeQueueAutoResolveRule> AutoResolve { get; set; } = [];
    public List<string> AutoResolveDefaults { get; set; } = [];
    public List<string> KeepBothDefaults { get; set; } = [];
    public int RetryFlaky { get; set; } = 1;
    public int ChecksTimeoutMinutes { get; set; } = 60;
    public List<MergeQueueConfigValue> Values { get; set; } = [];

    /// <summary>Every regenerate workflow the configuration names: <see cref="PostRebaseActionName"/> and each rule's
    /// action, distinct, ordinal (O36).</summary>
    public IReadOnlyList<string> Workflows()
        => [.. AutoResolve.Select(r => r.Action).Append(PostRebaseActionName).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The snapshot a run keeps (O15): this file half plus the owner's trust policy, each with its source.
    /// </summary>
    public MergeQueueConfigSnapshot Snapshot(string configRef, Repository repository, Account? account)
    {
        var (floor, floorSource) = Repository.ResolveMergeQueueApprovalFloor(repository, account);
        var (exclusions, exclusionsSource) = Repository.ResolveMergeQueueCheckExclusions(repository, account);
        var (bypass, bypassSource) = Repository.ResolveMergeQueueBypassRole(repository, account);
        return new MergeQueueConfigSnapshot
        {
            ConfigRef = configRef,
            Branches = [.. Branches],
            Label = Label,
            PostRebaseActionName = PostRebaseActionName,
            PostRebaseTimeoutMinutes = PostRebaseTimeoutMinutes,
            PostRebaseWhen = PostRebaseWhen,
            RegeneratedPaths = [.. RegeneratedPaths],
            AutoResolve =
            [
                .. AutoResolve.Select(r => new MergeQueueAutoResolveRule
                {
                    Path = r.Path,
                    Keys = [.. r.Keys],
                    Strategy = r.Strategy,
                    Validate = [.. r.Validate],
                    Regenerate = r.Regenerate,
                    Action = r.Action,
                    Source = r.Source == MergeQueueYml.Source ? $"{MergeQueueYml.Source} @ {configRef}" : r.Source,
                }),
            ],
            AutoResolveDefaults = [.. AutoResolveDefaults],
            KeepBothDefaults = [.. KeepBothDefaults],
            RetryFlaky = RetryFlaky,
            ChecksTimeoutMinutes = ChecksTimeoutMinutes,
            ApprovalFloor = floor,
            CheckExclusions = [.. exclusions],
            BypassRole = bypass,
            Values =
            [
                .. Values.Select(v => new MergeQueueConfigValue { Key = v.Key, Value = v.Value, Source = v.Source == MergeQueueYml.Source ? $"{MergeQueueYml.Source} @ {configRef}" : v.Source }),
                new MergeQueueConfigValue { Key = "approvalFloor", Value = floor.ToString(), Source = floorSource },
                new MergeQueueConfigValue { Key = "checkExclusions", Value = exclusions.Count == 0 ? "(none)" : string.Join(", ", exclusions), Source = exclusionsSource },
                new MergeQueueConfigValue { Key = "bypassRole", Value = bypass.ToString(), Source = bypassSource },
            ],
        };
    }
}
