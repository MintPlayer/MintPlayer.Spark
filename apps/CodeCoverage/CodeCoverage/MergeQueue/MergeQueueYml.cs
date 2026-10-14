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
///   autoResolve:
///     - path: "**/App_Data/modelHashes.json"
///       strategy: take-base          # the only strategy (O1); keys: [..] limits it to those JSON members (O2)
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

        if (file.AutoResolve is { Count: > MergeQueueWireRules.MaxGlobs } tooMany)
            errors.Add($"mergeQueue.autoResolve holds {tooMany.Count} entries; the queue's git writer takes at most {MergeQueueWireRules.MaxGlobs}.");
        foreach (var rule in file.AutoResolve ?? [])
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
            // O1: json-union is dropped; anything a person writes is never merged by the bot.
            if (rule!.Strategy is not (null or "take-base"))
            {
                errors.Add($"mergeQueue.autoResolve '{path}': strategy '{rule.Strategy}' is not supported; only take-base is (a conflict anywhere else evicts).");
                continue;
            }
            List<string> keys = [.. (rule.Keys ?? []).Select(k => k?.Trim() ?? string.Empty).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)];
            if (keys.Count > MergeQueueWireRules.MaxGlobs)
                errors.Add($"mergeQueue.autoResolve '{path}' lists {keys.Count} keys; the queue's git writer takes at most {MergeQueueWireRules.MaxGlobs}.");
            else if (keys.Any(k => !MergeQueueWireRules.IsKey(k)))
                errors.Add($"mergeQueue.autoResolve '{path}' has a key the queue's git writer does not accept: {MergeQueueWireRules.KeyRule}.");
            section.AutoResolve.Add(new MergeQueueAutoResolveRule { Path = path, Keys = keys });
        }
        Value("autoResolve", section.AutoResolve.Count == 0
            ? "(none: every conflict evicts)"
            : string.Join(", ", section.AutoResolve.Select(r => r.Keys.Count == 0 ? $"{r.Path} (take-base, any line)" : $"{r.Path} (take-base: {string.Join(", ", r.Keys)})")),
            file.AutoResolve is not null);

        // §5.2: take-base leaves the generated files stale on purpose, so the regenerate step is not optional.
        if (section.AutoResolve.Count > 0 && section.PostRebaseActionName is null)
            errors.Add("mergeQueue.autoResolve takes the base side of generated files, which only the regenerate workflow can fix: set mergeQueue.postRebaseActionName.");
        if (section.AutoResolve.Count > 0 && section.PostRebaseWhen == EPostRebaseWhen.Never)
            errors.Add("mergeQueue.postRebaseWhen 'never' cannot be combined with autoResolve: a taken base side is never regenerated.");
        if (section.PostRebaseWhen == EPostRebaseWhen.Always && section.PostRebaseActionName is null)
            errors.Add("mergeQueue.postRebaseWhen 'always' needs mergeQueue.postRebaseActionName.");
        if (section.PostRebaseActionName is not null && section.PostRebaseWhen != EPostRebaseWhen.Never && section.RegeneratedPaths.Count == 0)
            errors.Add("mergeQueue.regeneratedPaths is empty, so every regenerate patch would be refused (T4).");

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
        public List<FileAutoResolve>? AutoResolve { get; set; }
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
    public List<MergeQueueAutoResolveRule> AutoResolve { get; set; } = [];
    public int RetryFlaky { get; set; } = 1;
    public int ChecksTimeoutMinutes { get; set; } = 60;
    public List<MergeQueueConfigValue> Values { get; set; } = [];

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
            AutoResolve = [.. AutoResolve.Select(r => new MergeQueueAutoResolveRule { Path = r.Path, Keys = [.. r.Keys] })],
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
