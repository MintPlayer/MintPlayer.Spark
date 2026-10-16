using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>The planned edits a workflow or <c>action.yml</c> change may hold (R15): whole <c>uses:</c> values, and
/// version values of the closed list of setup-action inputs.</summary>
/// <param name="PlannedUses">(old, new) <c>uses:</c> values, e.g. (<c>actions/checkout@v4</c>, <c>actions/checkout@&lt;sha&gt;</c>).</param>
/// <param name="PlannedWith">(input key, old value, new value) of a setup action's version input.</param>
public sealed record WorkflowChangeRules(IReadOnlySet<(string Old, string New)> PlannedUses, IReadOnlySet<(string Key, string Old, string New)> PlannedWith)
{
    /// <summary>The target <c>action.yml</c> per new <c>uses:</c> value (<c>owner/repo[/path]@ref</c>), when the caller read
    /// it; a step whose <c>uses:</c> changed to one of these must pass only inputs it declares, pass every required input
    /// without a default, and be read only for outputs it declares.</summary>
    public IReadOnlyDictionary<string, string>? TargetActions { get; init; }
}

/// <summary>One scalar the change altered, at a dotted path such as <c>jobs.build.steps[1].uses</c>.</summary>
public sealed record WorkflowChange(string Path, string Old, string New, long Line);

public sealed record WorkflowChangeVerdict(ContentVerdict Verdict, IReadOnlyList<WorkflowChange> Changes);

/// <summary>
/// <c>github-workflow</c> (docs/code-coverage/dependency-updates-PRD.md R15, gap C). As a content validator: a
/// <see cref="YamlValidator"/>-valid document that is a workflow (<c>jobs:</c>, each job with <c>steps:</c> or a reusable
/// workflow's <c>uses:</c>) or an action (<c>runs:</c>), whose every <c>uses:</c> is well-formed: <c>./local</c>,
/// <c>docker://image</c> or <c>owner/repo[/path]@ref</c> in ASCII. With <see cref="ValidateChange"/>, the dependency
/// updater's before/after comparison: both parsed, aliases expanded, and the only scalars that may differ are planned
/// <c>uses:</c> values and planned version inputs of <see cref="SetupActionVersionKeys"/>; any other difference, a changed
/// node in flow style, under an anchor or reached through an alias, is refused. The runner's own actionlint verdict is
/// advisory only: the runner ran untrusted code.
/// </summary>
[Register(typeof(IContentValidator), ServiceLifetime.Singleton)]
public sealed partial class GitHubWorkflowValidator : IContentValidator
{
    /// <summary>The closed, code-defined list of setup actions and the inputs whose version value the updater may change
    /// (R15). Never configurable in <c>coverage.yml</c>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> SetupActionVersionKeys { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["actions/setup-dotnet"] = new HashSet<string>(StringComparer.Ordinal) { "dotnet-version" },
            ["actions/setup-node"] = new HashSet<string>(StringComparer.Ordinal) { "node-version" },
            ["actions/setup-python"] = new HashSet<string>(StringComparer.Ordinal) { "python-version" },
            ["actions/setup-java"] = new HashSet<string>(StringComparer.Ordinal) { "java-version" },
            ["actions/setup-go"] = new HashSet<string>(StringComparer.Ordinal) { "go-version" },
            ["ruby/setup-ruby"] = new HashSet<string>(StringComparer.Ordinal) { "ruby-version" },
        };

    /// <summary>Every key of <see cref="SetupActionVersionKeys"/>.</summary>
    public static IReadOnlySet<string> VersionKeys { get; } = SetupActionVersionKeys.Values.SelectMany(k => k).ToHashSet(StringComparer.Ordinal);

    /// <summary><c>owner/repo[/path]@ref</c>, ASCII only (a homoglyph owner never matches).</summary>
    [GeneratedRegex(@"\A(?<action>[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}(?:/[A-Za-z0-9._/-]{1,300})?)@(?<ref>[A-Za-z0-9._/+-]{1,255})\z", RegexOptions.CultureInvariant)]
    public static partial Regex RemoteUses();

    [GeneratedRegex(@"\A\./[A-Za-z0-9._/-]{0,500}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalUses();

    [GeneratedRegex(@"\Adocker://[A-Za-z0-9._/:@+-]{1,500}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DockerUses();

    /// <summary>A setup action's version value: one token, no expression (<c>${{</c>), no line break.</summary>
    [GeneratedRegex(@"\A[0-9A-Za-z][0-9A-Za-z.+*_/-]{0,63}\z", RegexOptions.CultureInvariant)]
    public static partial Regex VersionValue();

    [GeneratedRegex(@"steps\.(?<id>[A-Za-z0-9_-]+)\.outputs\.(?<name>[A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OutputReference();

    public string Name => MergeQueueWireRules.GitHubWorkflow;

    public ContentVerdict Validate(string text) => Check(text).Verdict;

    /// <summary>The kind of file a valid document is.</summary>
    public enum FileKind { Workflow, Action }

    /// <summary>The structure check of <see cref="Validate"/>, with the parsed root when it passes.</summary>
    public static (YamlMapping? Root, FileKind Kind, ContentVerdict Verdict) Check(string text)
    {
        var (document, verdict) = YamlDocument.Parse(text);
        if (document is null)
            return (null, default, verdict);
        if (document.Roots.Count != 1 || YamlDocument.Resolve(document.Roots[0]) is not YamlMapping root)
            return (null, default, ContentVerdict.Invalid("not one YAML document whose root is a mapping", 1, 1));
        if (root["jobs"] is { } jobsNode)
        {
            if (YamlDocument.Resolve(jobsNode) is not YamlMapping jobs)
                return (null, default, ContentVerdict.Invalid("jobs is not a mapping", jobsNode.Line, jobsNode.Column));
            foreach (var (name, jobNode) in jobs.Entries)
            {
                if (YamlDocument.Resolve(jobNode) is not YamlMapping job)
                    return (null, default, ContentVerdict.Invalid("a job that is not a mapping", jobNode.Line, jobNode.Column));
                if (job["uses"] is { } reusable)
                {
                    if (!IsReusableWorkflow(reusable))
                        return (null, default, ContentVerdict.Invalid("a job's uses: that is not a reusable workflow reference", reusable.Line, reusable.Column));
                }
                else if (job["steps"] is not { } steps)
                    return (null, default, ContentVerdict.Invalid("a job with neither steps: nor uses:", name.Line, name.Column));
                else if (CheckSteps(steps) is { } bad)
                    return (null, default, bad);
            }
            return (root, FileKind.Workflow, ContentVerdict.Ok);
        }
        if (root["runs"] is { } runsNode)
        {
            if (YamlDocument.Resolve(runsNode) is not YamlMapping runs || runs["using"] is not { } usingNode || YamlDocument.Resolve(usingNode) is not YamlScalar)
                return (null, default, ContentVerdict.Invalid("runs: is not a mapping with using:", runsNode.Line, runsNode.Column));
            if (runs["steps"] is { } steps && CheckSteps(steps) is { } bad)
                return (null, default, bad);
            return (root, FileKind.Action, ContentVerdict.Ok);
        }
        return (null, default, ContentVerdict.Invalid("neither a workflow (jobs:) nor an action (runs:)", root.Line, root.Column));
    }

    private static ContentVerdict? CheckSteps(YamlNode stepsNode)
    {
        if (YamlDocument.Resolve(stepsNode) is not YamlSequence steps)
            return ContentVerdict.Invalid("steps: is not a sequence", stepsNode.Line, stepsNode.Column);
        foreach (var stepNode in steps.Items)
        {
            if (YamlDocument.Resolve(stepNode) is not YamlMapping step)
                return ContentVerdict.Invalid("a step that is not a mapping", stepNode.Line, stepNode.Column);
            if (step["uses"] is { } uses)
            {
                if (YamlDocument.Resolve(uses) is not YamlScalar { IsBlock: false } scalar
                    || !(RemoteUses().IsMatch(scalar.Value) || LocalUses().IsMatch(scalar.Value) || DockerUses().IsMatch(scalar.Value)))
                    return ContentVerdict.Invalid("a step's uses: that is not ./path, docker://image or owner/repo@ref", uses.Line, uses.Column);
            }
            else if (step["run"] is null)
                return ContentVerdict.Invalid("a step with neither uses: nor run:", step.Line, step.Column);
        }
        return null;
    }

    private static bool IsReusableWorkflow(YamlNode node)
        => YamlDocument.Resolve(node) is YamlScalar { IsBlock: false } scalar
           && (RemoteUses().Match(scalar.Value) is { Success: true } m && m.Groups["action"].Value.Contains("/.github/workflows/", StringComparison.Ordinal)
               || LocalUses().IsMatch(scalar.Value) && scalar.Value.StartsWith("./.github/workflows/", StringComparison.Ordinal));

    // ═══ The before/after comparison ═══════════════════════════════════════════════════════════════

    private sealed class Refused(ContentVerdict verdict) : Exception
    {
        public ContentVerdict Verdict { get; } = verdict;
    }

    /// <summary>
    /// Compares <paramref name="before"/> and <paramref name="after"/> with aliases expanded: the same kinds, tags,
    /// anchors, keys (in order, with their styles), sequence lengths and collection styles everywhere, and scalars equal
    /// in value and style except planned <c>uses:</c> values (a step's or a job's) and planned version inputs under a
    /// closed-list setup action's <c>with:</c>. A changed scalar in flow style, anchored (or below an anchor), or reached
    /// through an alias is refused, so one edit can never change two places. Then the target <c>action.yml</c> checks of
    /// <see cref="WorkflowChangeRules.TargetActions"/>.
    /// </summary>
    public static WorkflowChangeVerdict ValidateChange(string before, string after, WorkflowChangeRules rules)
    {
        var (beforeRoot, beforeKind, beforeVerdict) = Check(before);
        if (beforeRoot is null)
            return new(ContentVerdict.Invalid($"the file before the change fails: {beforeVerdict.Problem}", beforeVerdict.Line, beforeVerdict.Column), []);
        var (afterRoot, afterKind, afterVerdict) = Check(after);
        if (afterRoot is null)
            return new(afterVerdict, []);
        if (beforeKind != afterKind)
            return new(ContentVerdict.Invalid("the file changed from a workflow to an action or back", 1, 1), []);
        var changes = new List<WorkflowChange>();
        try
        {
            Compare(beforeRoot, afterRoot, [], new Inherited(false, false, false), afterRoot, rules, changes);
            CheckTargets(afterRoot, afterKind, changes, rules);
        }
        catch (Refused refused)
        {
            return new(refused.Verdict, changes);
        }
        return new(ContentVerdict.Ok, changes);
    }

    private readonly record struct Inherited(bool ViaAlias, bool Anchored, bool Flow);

    private static void Compare(YamlNode before, YamlNode after, List<string> path, Inherited inherited, YamlMapping afterRoot,
        WorkflowChangeRules rules, List<WorkflowChange> changes)
    {
        var here = inherited with { ViaAlias = inherited.ViaAlias || before is YamlAlias || after is YamlAlias };
        var b = YamlDocument.Resolve(before);
        var a = YamlDocument.Resolve(after);
        if (b.GetType() != a.GetType())
            throw new Refused(ContentVerdict.Invalid("a node changed kind", a.Line, a.Column));
        if (b.Tag != a.Tag)
            throw new Refused(ContentVerdict.Invalid("a node's tag changed", a.Line, a.Column));
        if (b.Anchor != a.Anchor)
            throw new Refused(ContentVerdict.Invalid("an anchor was added, removed or renamed", a.Line, a.Column));
        here = here with { Anchored = here.Anchored || a.Anchor is not null };
        switch (b, a)
        {
            case (YamlMapping bm, YamlMapping am):
                if (bm.IsFlow != am.IsFlow || bm.Entries.Count != am.Entries.Count)
                    throw new Refused(ContentVerdict.Invalid("a mapping's keys or style changed", am.Line, am.Column));
                for (var i = 0; i < bm.Entries.Count; i++)
                {
                    var (bk, bv) = bm.Entries[i];
                    var (ak, av) = am.Entries[i];
                    if (bk.Value != ak.Value || bk.Style != ak.Style || bk.Anchor != ak.Anchor || bk.Tag != ak.Tag)
                        throw new Refused(ContentVerdict.Invalid("a mapping's keys or style changed", ak.Line, ak.Column));
                    path.Add(ak.Value);
                    Compare(bv, av, path, here with { Flow = here.Flow || am.IsFlow }, afterRoot, rules, changes);
                    path.RemoveAt(path.Count - 1);
                }
                return;
            case (YamlSequence bs, YamlSequence @as):
                if (bs.IsFlow != @as.IsFlow || bs.Items.Count != @as.Items.Count)
                    throw new Refused(ContentVerdict.Invalid("a sequence's length or style changed", @as.Line, @as.Column));
                for (var i = 0; i < bs.Items.Count; i++)
                {
                    path.Add($"[{i}]");
                    Compare(bs.Items[i], @as.Items[i], path, here with { Flow = here.Flow || @as.IsFlow }, afterRoot, rules, changes);
                    path.RemoveAt(path.Count - 1);
                }
                return;
            case (YamlScalar bsc, YamlScalar asc):
                if (bsc.Value == asc.Value && bsc.Style == asc.Style)
                    return;
                if (here.ViaAlias || here.Anchored || here.Flow)
                    throw new Refused(ContentVerdict.Invalid("a changed value in flow style, under an anchor or reached through an alias", asc.Line, asc.Column));
                if (bsc.Style != asc.Style || asc.IsBlock)
                    throw new Refused(ContentVerdict.Invalid("a changed value whose scalar style changed or is a block scalar", asc.Line, asc.Column));
                if (!IsPlanned(path, bsc.Value, asc.Value, afterRoot, rules))
                    throw new Refused(ContentVerdict.Invalid("a change other than a planned uses: or setup-action version value", asc.Line, asc.Column));
                changes.Add(new WorkflowChange(string.Join('.', path).Replace(".[", "[", StringComparison.Ordinal), bsc.Value, asc.Value, asc.Line));
                return;
        }
    }

    private static bool IsUsesPath(List<string> p)
        => p.Count == 5 && p[0] == "jobs" && p[2] == "steps" && IsIndex(p[3]) && p[4] == "uses"
           || p.Count == 3 && p[0] == "jobs" && p[2] == "uses"
           || p.Count == 4 && p[0] == "runs" && p[1] == "steps" && IsIndex(p[2]) && p[3] == "uses";

    /// <summary>The path of the step a <c>with.&lt;key&gt;</c> path is under, or null.</summary>
    private static List<string>? WithStepPath(List<string> p)
    {
        if (p.Count == 6 && p[0] == "jobs" && p[2] == "steps" && IsIndex(p[3]) && p[4] == "with")
            return p[..4];
        if (p.Count == 5 && p[0] == "runs" && p[1] == "steps" && IsIndex(p[2]) && p[3] == "with")
            return p[..3];
        return null;
    }

    private static bool IsIndex(string segment) => segment.Length > 2 && segment[0] == '[' && segment[^1] == ']';

    private static bool IsPlanned(List<string> path, string old, string @new, YamlMapping afterRoot, WorkflowChangeRules rules)
    {
        if (IsUsesPath(path))
        {
            return rules.PlannedUses.Contains((old, @new))
                   && RemoteUses().Match(old) is { Success: true } o && RemoteUses().Match(@new) is { Success: true } n
                   && o.Groups["action"].Value == n.Groups["action"].Value;
        }
        if (WithStepPath(path) is { } stepPath && NodeAt(afterRoot, stepPath) is YamlMapping step
            && step["uses"] is { } usesNode && YamlDocument.Resolve(usesNode) is YamlScalar uses
            && RemoteUses().Match(uses.Value) is { Success: true } action
            && SetupActionVersionKeys.TryGetValue(action.Groups["action"].Value, out var keys) && keys.Contains(path[^1]))
        {
            return VersionValue().IsMatch(old) && VersionValue().IsMatch(@new) && rules.PlannedWith.Contains((path[^1], old, @new));
        }
        return false;
    }

    private static YamlNode? NodeAt(YamlNode root, IEnumerable<string> path)
    {
        YamlNode? node = root;
        foreach (var segment in path)
        {
            node = YamlDocument.Resolve(node!) switch
            {
                YamlMapping m => m[segment],
                YamlSequence s when IsIndex(segment) && int.TryParse(segment[1..^1], out var i) && i < s.Items.Count => s.Items[i],
                _ => null,
            };
            if (node is null)
                return null;
        }
        return YamlDocument.Resolve(node!);
    }

    // ═══ The target action's interface (R15: inputs exist, required inputs passed, outputs exist) ═══════

    private static void CheckTargets(YamlMapping afterRoot, FileKind kind, List<WorkflowChange> changes, WorkflowChangeRules rules)
    {
        if (rules.TargetActions is not { Count: > 0 } targets)
            return;
        foreach (var change in changes.Where(c => c.Path.EndsWith(".uses", StringComparison.Ordinal) && c.Path.Contains("steps[", StringComparison.Ordinal)))
        {
            if (!targets.TryGetValue(change.New, out var actionText))
                continue;
            var stepPath = SplitPath(change.Path)[..^1];
            if (NodeAt(afterRoot, stepPath) is not YamlMapping step)
                continue;
            var (actionDocument, actionVerdict) = YamlDocument.Parse(actionText);
            if (actionDocument is null || actionDocument.Roots.Count != 1 || YamlDocument.Resolve(actionDocument.Roots[0]) is not YamlMapping action)
                throw new Refused(ContentVerdict.Invalid($"the target action.yml of a changed uses: does not parse ({actionVerdict.Problem ?? "not one mapping"})", change.Line, null));
            var inputs = Keys(action["inputs"]);
            var outputs = Keys(action["outputs"]);
            var with = step["with"] is { } withNode && YamlDocument.Resolve(withNode) is YamlMapping w ? w : null;
            var passed = with?.Entries.Select(e => e.Key.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (var key in passed)
            {
                if (!inputs.ContainsKey(key))
                    throw new Refused(ContentVerdict.Invalid("a with: key the target action.yml does not declare as an input", change.Line, null));
            }
            foreach (var (name, input) in inputs)
            {
                if (input is YamlMapping definition && definition["required"] is { } required && YamlDocument.Resolve(required) is YamlScalar { Value: "true" }
                    && definition["default"] is null && !passed.Contains(name))
                    throw new Refused(ContentVerdict.Invalid("a required input of the target action.yml, without a default, is not passed", change.Line, null));
            }
            if (step["id"] is { } idNode && YamlDocument.Resolve(idNode) is YamlScalar id)
            {
                var scope = kind == FileKind.Workflow ? NodeAt(afterRoot, stepPath[..2]) : afterRoot;
                foreach (var scalar in Scalars(scope))
                {
                    foreach (Match reference in OutputReference().Matches(scalar.Value))
                    {
                        if (reference.Groups["id"].Value == id.Value && !outputs.ContainsKey(reference.Groups["name"].Value))
                            throw new Refused(ContentVerdict.Invalid("an output of the changed step that the target action.yml does not declare", scalar.Line, scalar.Column));
                    }
                }
            }
        }
    }

    private static List<string> SplitPath(string path)
    {
        var segments = new List<string>();
        foreach (var part in path.Split('.'))
        {
            var bracket = part.IndexOf('[', StringComparison.Ordinal);
            if (bracket < 0)
            {
                segments.Add(part);
                continue;
            }
            segments.Add(part[..bracket]);
            segments.Add(part[bracket..]);
        }
        return segments;
    }

    private static Dictionary<string, YamlNode> Keys(YamlNode? node)
    {
        var map = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);
        if (node is not null && YamlDocument.Resolve(node) is YamlMapping mapping)
        {
            foreach (var (key, value) in mapping.Entries)
                map[key.Value] = YamlDocument.Resolve(value);
        }
        return map;
    }

    private static IEnumerable<YamlScalar> Scalars(YamlNode? node)
    {
        switch (node is null ? null : YamlDocument.Resolve(node))
        {
            case YamlScalar scalar:
                yield return scalar;
                break;
            case YamlMapping mapping:
                foreach (var (key, value) in mapping.Entries)
                {
                    yield return key;
                    foreach (var s in Scalars(value))
                        yield return s;
                }
                break;
            case YamlSequence sequence:
                foreach (var item in sequence.Items)
                {
                    foreach (var s in Scalars(item))
                        yield return s;
                }
                break;
        }
    }
}
