using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Forge;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CodeCoverage.MergeQueue;

/// <summary>One reason the queue may not push a fork's code to a queue branch (merge-queue PRD §0 D65).</summary>
/// <param name="Path">The workflow file.</param>
/// <param name="Ref">Where it was read: the pull request's base branch for a push trigger, the default branch otherwise.</param>
/// <param name="Line">The line the problem starts on, when there is one.</param>
public sealed record ForkGuardFinding(string Path, string Ref, int? Line, string Problem, string Fix)
{
    public override string ToString() => $"`{Path}` on `{Ref}`{(Line is { } line ? $", line {line}" : string.Empty)}: {Problem} {Fix}";
}

/// <summary>
/// The fork precondition of a dispatch (merge-queue PRD §0 D65). A dispatch runs the regenerate workflow on
/// <c>mq/&lt;base&gt;/pr-&lt;n&gt;</c>, so the queue must first push the prepared commit there, and for a fork's pull request
/// that commit holds the fork's code. A push to a branch of the base repository starts that repository's <c>push</c>
/// workflows on the pushed commit, with the repository's secrets (D14, D19: an App's push does too), and a run of the
/// regenerate workflow can start <c>workflow_run</c> workflows with them as well. The queue pushes a fork's commit only
/// when the workflows show that none of this happens:
/// <list type="number">
/// <item>No workflow of the pushed commit runs on a push that creates the queue branch: a <c>push</c> trigger whose branch
/// filter leaves the branch out, or one for tags only; no <c>create</c> trigger. The pushed commit's <c>.github/</c> is
/// the base commit's (the writer refuses a fork's commit that changes it, D63/D65), so these are read at the base commit:
/// GitHub reads a push run's workflow from the pushed commit, not from the default branch.</item>
/// <item>The regenerate workflow (the default branch's, which D12 holds the queue branch's copy to) gives every job
/// <c>contents: read</c> at most, references no secret, asks for no OIDC token and runs on GitHub-hosted runners only.</item>
/// <item>No default-branch workflow runs on the regenerate workflow's runs on the queue branch (<c>workflow_run</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Conservative throughout.</b> Anything the guard cannot evaluate with certainty counts as "would run": a file it
/// cannot read or parse, a merge key, an unknown filter key, a filter pattern with a character GitHub does not document.
/// A pattern that makes a workflow run is matched generously (case-insensitively, also against <c>refs/heads/…</c>, and
/// <c>.</c> as any character); one that keeps it from running is matched strictly (case-sensitively, on the short name
/// only, <c>.</c> literally). <c>paths</c> filters never make a push trigger safe: the fork chooses the pushed paths.
/// </para>
/// <para>
/// <b>Not evaluated:</b> triggers that run the default branch's own code with the queue commit only as event data
/// (<c>check_run</c>, <c>check_suite</c>, <c>status</c>). They run fork code only if their steps check it out
/// explicitly, which no filter could rule out. A self-hosted runner registered under a GitHub-hosted label's name cannot
/// be seen from the repository either.
/// </para>
/// </remarks>
public static partial class MergeQueueForkGuard
{
    /// <summary>The eviction reason when the precondition fails.</summary>
    public const string ReasonCode = "fork-unsafe-workflows";

    private const string BranchFix = "Add `branches-ignore: ['mq/**']` under its `{0}:` trigger (or, with a `branches:` list, a last entry `'!mq/**'`).";

    /// <summary>Every finding of the three checks; empty means the queue may push the fork's commit and dispatch.</summary>
    /// <param name="pushed">The workflows of the commit the queue would push, read at the base commit.</param>
    /// <param name="baseBranch">The pull request's base branch, to name where <paramref name="pushed"/> were read.</param>
    /// <param name="atDefault">The default branch's workflows.</param>
    /// <param name="regenerateFile">The regenerate workflow's file name (<c>postRebaseActionName</c>).</param>
    /// <param name="queueBranch">The concrete queue branch, <c>mq/&lt;base&gt;/pr-&lt;n&gt;</c>.</param>
    public static IReadOnlyList<ForkGuardFinding> Evaluate(
        IReadOnlyList<ForgeWorkflowFile> pushed, string baseBranch, IReadOnlyList<ForgeWorkflowFile> atDefault, string defaultBranch,
        string regenerateFile, string queueBranch)
    {
        var findings = new List<ForkGuardFinding>();
        foreach (var file in pushed)
            PushTriggers(file, baseBranch, queueBranch, findings);

        var regeneratePath = $".github/workflows/{regenerateFile}";
        var regenerate = atDefault.FirstOrDefault(f => string.Equals(f.Path, regeneratePath, StringComparison.Ordinal));
        string? regenerateName = null;
        if (regenerate is null)
            findings.Add(new(regeneratePath, defaultBranch, null, "The regenerate workflow is not there.", "Add it from the setup guide's template."));
        else if (Load(regenerate, defaultBranch, findings) is { } root)
        {
            RegenerateRights(regenerate.Path, defaultBranch, root, findings);
            // GitHub names a workflow by its `name:`, or by its path when it has none.
            regenerateName = Child(root, "name") is YamlScalarNode { Value: { Length: > 0 } name } ? name : regeneratePath;
        }

        foreach (var file in atDefault)
            ChainedRuns(file, defaultBranch, regenerateName, regeneratePath, queueBranch, findings);
        return findings;
    }

    // ═══ (1) Push triggers ═══════════════════════════════════════════════════════════════════════════

    private static void PushTriggers(ForgeWorkflowFile file, string where, string queueBranch, List<ForkGuardFinding> findings)
    {
        if (Load(file, where, findings) is not { } root || Triggers(file, where, root, findings) is not { } triggers)
            return;
        foreach (var (name, node, key) in triggers)
        {
            if (name == "create")
                findings.Add(new(file.Path, where, Line(key), $"`on: create` runs whenever a branch is created, `{queueBranch}` included, on its commit.",
                    "Remove the `create` trigger, or do its work on a `push` trigger whose branch filter leaves out `mq/**`."));
            else if (name == "push" && PushRuns(node, queueBranch) is { } runs)
                findings.Add(new(file.Path, where, Line(key), $"`on: push` {runs}, so a push of `{queueBranch}` starts it on the pushed commit, with this repository's secrets.",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, BranchFix, "push")));
        }
    }

    /// <summary>Why a <c>push</c> trigger runs for a push of <paramref name="branch"/>, or null when it certainly does not.</summary>
    private static string? PushRuns(YamlNode? config, string branch)
    {
        if (IsNull(config))
            return "has no branch filter";
        if (config is not YamlMappingNode filters)
            return "has a filter the queue cannot read";
        string[] known = ["branches", "branches-ignore", "tags", "tags-ignore", "paths", "paths-ignore"];
        if (filters.Children.Keys.FirstOrDefault(k => k is not YamlScalarNode { Value: var v } || !known.Contains(v, StringComparer.Ordinal)) is { } unknown)
            return $"has a key the queue does not know ({Text(unknown)})";
        var branches = Child(filters, "branches");
        var ignore = Child(filters, "branches-ignore");
        if (branches is null && ignore is null)
            // Tags only: GitHub runs it for tag pushes alone. Otherwise every branch, whatever the paths say: the fork
            // chooses the pushed paths.
            return Child(filters, "tags") is not null || Child(filters, "tags-ignore") is not null ? null : "has no branch filter (paths filters do not count)";
        if (branches is not null && ignore is not null)
            return "has both `branches` and `branches-ignore`, which GitHub does not allow";
        return branches is not null ? BranchesRun(branches, branch) : BranchesIgnoreRuns(ignore!, branch);
    }

    // ═══ (2) The regenerate workflow's own rights ════════════════════════════════════════════════════

    private static void RegenerateRights(string path, string where, YamlMappingNode root, List<ForkGuardFinding> findings)
    {
        void Add(YamlNode? at, string problem, string fix) => findings.Add(new(path, where, at is null ? null : Line(at), problem, fix));
        const string ReadOnlyFix = "Give the workflow `permissions: contents: read` at the top, and nothing more in any job.";

        var top = Child(root, "permissions");
        if (Child(root, "jobs") is not YamlMappingNode jobs || jobs.Children.Count == 0)
        {
            Add(Child(root, "jobs"), "The regenerate workflow has no `jobs:` the queue can read.", "Use the setup guide's template.");
            return;
        }
        foreach (var (key, value) in jobs.Children)
        {
            var job = Text(key);
            if (value is not YamlMappingNode body)
            {
                Add(key, $"Job `{job}` is not a mapping the queue can read.", "Use the setup guide's template.");
                continue;
            }
            if (Child(body, "uses") is { } uses)
                Add(uses, $"Job `{job}` calls a reusable workflow, which the queue cannot read.", "Put the steps in the job itself, as the template does.");
            if (Child(body, "environment") is { } environment)
                Add(environment, $"Job `{job}` uses an environment, whose secrets and rules then apply to the pull request's code.", "Remove `environment:`.");

            var permissions = Child(body, "permissions") ?? top;
            if (permissions is null)
                Add(key, $"Job `{job}` has no `permissions:`, and the workflow none at the top, so it gets the repository's default token, which may write.", ReadOnlyFix);
            else
                foreach (var (at, problem) in PermissionProblems(permissions))
                    Add(at, $"Job `{job}`: {problem}", ReadOnlyFix);

            foreach (var (at, problem) in RunnerProblems(Child(body, "runs-on"), body))
                Add(at, $"Job `{job}`: {problem}", "Run it on a GitHub-hosted runner such as `ubuntu-latest`: the pull request's code would run on your machine.");
        }

        foreach (var (at, problem) in SecretReferences(root))
            Add(at, problem, "Remove every secret from the regenerate workflow: it runs the pull request's code.");
    }

    /// <summary>What a <c>permissions:</c> value grants beyond <c>contents: read</c>; empty when nothing.</summary>
    private static IEnumerable<(YamlNode At, string Problem)> PermissionProblems(YamlNode permissions)
    {
        if (permissions is YamlScalarNode scalar)
        {
            yield return (scalar, scalar.Value is "read-all" or "write-all"
                ? $"`permissions: {scalar.Value}` grants more than `contents: read`."
                : "its `permissions:` is not a mapping the queue can read.");
            yield break;
        }
        if (permissions is not YamlMappingNode scopes)
        {
            yield return (permissions, "its `permissions:` is not a mapping the queue can read.");
            yield break;
        }
        foreach (var (key, value) in scopes.Children)
        {
            var scope = Text(key);
            var level = value is YamlScalarNode { Value: { } v } ? v : null;
            if (level is not ("read" or "write" or "none"))
                yield return (value, $"`{scope}: {Text(value)}` is not a level the queue can read.");
            else if (scope == "id-token" && level != "none")
                yield return (key, $"`id-token: {level}` lets the pull request's code ask for an OIDC token in this repository's name.");
            else if (scope == "contents" ? level == "write" : level != "none")
                yield return (key, $"`{scope}: {level}` grants more than `contents: read`.");
        }
    }

    /// <summary>What may put a job on a self-hosted runner; empty for GitHub-hosted labels only.</summary>
    private static IEnumerable<(YamlNode At, string Problem)> RunnerProblems(YamlNode? runsOn, YamlNode job)
    {
        if (runsOn is null)
        {
            yield return (job, "it names no `runs-on:` the queue can read.");
            yield break;
        }
        var labels = new List<YamlNode>();
        switch (runsOn)
        {
            case YamlScalarNode:
                labels.Add(runsOn);
                break;
            case YamlSequenceNode sequence:
                labels.AddRange(sequence.Children);
                break;
            case YamlMappingNode map:
                if (Child(map, "group") is { } group)
                    yield return (group, "it runs in a runner group, which may hold self-hosted runners.");
                switch (Child(map, "labels"))
                {
                    case YamlScalarNode one: labels.Add(one); break;
                    case YamlSequenceNode many: labels.AddRange(many.Children); break;
                    case { } other: yield return (other, "its runner labels are not a list the queue can read."); break;
                }
                break;
        }
        foreach (var label in labels)
        {
            var text = label is YamlScalarNode { Value: { } v } ? v : null;
            if (text is null || text.Contains("${{", StringComparison.Ordinal))
                yield return (label, "its runner is chosen by an expression or a value the queue cannot read.");
            else if (string.Equals(text, "self-hosted", StringComparison.OrdinalIgnoreCase))
                yield return (label, "it runs on a self-hosted runner.");
            else if (!GitHubHostedLabel().IsMatch(text))
                yield return (label, $"`{text}` is not a GitHub-hosted runner's label, so it may select a self-hosted runner.");
        }
    }

    /// <summary>Every place the workflow reads the <c>secrets</c> context or passes secrets on: in an expression, an
    /// <c>if:</c> (an expression without the braces), or a <c>secrets:</c> key. Comments are not read.</summary>
    private static IEnumerable<(YamlNode At, string Problem)> SecretReferences(YamlMappingNode root)
    {
        foreach (var node in Nodes(root))
        {
            if (node is not YamlMappingNode map)
                continue;
            foreach (var (key, value) in map.Children)
            {
                var name = key is YamlScalarNode { Value: { } k } ? k : null;
                if (name == "secrets")
                    yield return (key, "It passes secrets on (`secrets:`).");
                if (value is YamlScalarNode { Value: { } text } && (name == "if" ? SecretsWord().IsMatch(text) : Expressions(text).Any(SecretsWord().IsMatch)))
                    yield return (value, "It reads the `secrets` context.");
            }
        }
    }

    private static IEnumerable<string> Expressions(string text)
    {
        foreach (Match match in Expression().Matches(text))
            yield return match.Groups[1].Value;
    }

    // ═══ (3) Runs chained to the regenerate workflow ═════════════════════════════════════════════════

    private static void ChainedRuns(ForgeWorkflowFile file, string where, string? regenerateName, string regeneratePath, string queueBranch,
        List<ForkGuardFinding> findings)
    {
        if (Load(file, where, findings) is not { } root || Triggers(file, where, root, findings) is not { } triggers)
            return;
        foreach (var (name, node, key) in triggers)
        {
            if (name != "workflow_run")
                continue;
            if (WorkflowRunRuns(node, regenerateName, regeneratePath, queueBranch) is { } runs)
                findings.Add(new(file.Path, where, Line(key), $"`on: workflow_run` {runs}: it would start, with this repository's secrets, after the regenerate workflow ran the pull request's code on `{queueBranch}`.",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, BranchFix, "workflow_run")));
        }
    }

    /// <summary>Why a <c>workflow_run</c> trigger runs after the regenerate workflow's run on <paramref name="branch"/>, or
    /// null when it certainly does not.</summary>
    private static string? WorkflowRunRuns(YamlNode? config, string? regenerateName, string regeneratePath, string branch)
    {
        if (config is not YamlMappingNode filters)
            return "has no `workflows:` the queue can read";
        string[] known = ["workflows", "types", "branches", "branches-ignore"];
        if (filters.Children.Keys.FirstOrDefault(k => k is not YamlScalarNode { Value: var v } || !known.Contains(v, StringComparer.Ordinal)) is { } unknown)
            return $"has a key the queue does not know ({Text(unknown)})";
        if (Patterns(Child(filters, "workflows")) is not { Count: > 0 } workflows)
            return "has no `workflows:` the queue can read";
        bool CouldName(string workflow)
            => regenerateName is null || workflow.IndexOfAny(['*', '?', '+', '[', ']', '!', '\\']) >= 0
               || string.Equals(workflow, regenerateName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(workflow, regeneratePath, StringComparison.OrdinalIgnoreCase)
               || string.Equals(workflow, Path.GetFileName(regeneratePath), StringComparison.OrdinalIgnoreCase);
        if (!workflows.Any(CouldName))
            return null;

        var branches = Child(filters, "branches");
        var ignore = Child(filters, "branches-ignore");
        if (branches is null && ignore is null)
            return "follows the regenerate workflow on every branch";
        if (branches is not null && ignore is not null)
            return "has both `branches` and `branches-ignore`, which GitHub does not allow";
        return branches is not null ? BranchesRun(branches, branch) : BranchesIgnoreRuns(ignore!, branch);
    }

    // ═══ Branch filters (GitHub's filter patterns) ══════════════════════════════════════════════════

    /// <summary><c>branches:</c>, in order: a pattern includes, a <c>!pattern</c> excludes again, the last match wins.</summary>
    private static string? BranchesRun(YamlNode node, string branch)
    {
        if (Patterns(node) is not { Count: > 0 } patterns)
            return "has a `branches:` filter the queue cannot read";
        var included = false;
        var positive = false;
        foreach (var pattern in patterns)
        {
            var negated = pattern.StartsWith('!');
            positive |= !negated;
            switch (FilterPattern.Matches(negated ? pattern[1..] : pattern, branch, generous: !negated))
            {
                case null:
                    return $"has a branch pattern the queue cannot evaluate ({pattern})";
                case true:
                    included = !negated;
                    break;
            }
        }
        if (!positive)
            return "has only `!` patterns in `branches:`, which GitHub does not allow";
        return included ? "has a branch filter that matches it" : null;
    }

    /// <summary><c>branches-ignore:</c>: any match keeps the workflow from running.</summary>
    private static string? BranchesIgnoreRuns(YamlNode node, string branch)
    {
        if (Patterns(node) is not { Count: > 0 } patterns)
            return "has a `branches-ignore:` filter the queue cannot read";
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
                return $"has a `!` pattern in `branches-ignore:`, which the queue cannot evaluate ({pattern})";
            switch (FilterPattern.Matches(pattern, branch, generous: false))
            {
                case null:
                    return $"has a branch pattern the queue cannot evaluate ({pattern})";
                case true:
                    return null;
            }
        }
        return "has a `branches-ignore:` filter that leaves it in";
    }

    /// <summary>A filter's patterns: one scalar or a list of scalars; null when it is anything else.</summary>
    private static List<string>? Patterns(YamlNode? node) => node switch
    {
        YamlScalarNode { Value: { Length: > 0 } one } when !IsNull(node) => [one],
        YamlSequenceNode many when many.Children.All(c => c is YamlScalarNode { Value: { Length: > 0 } })
            => [.. many.Children.Select(c => ((YamlScalarNode)c).Value!)],
        _ => null,
    };

    /// <summary>
    /// GitHub's filter patterns (docs.github.com, "Filter pattern cheat sheet"): <c>*</c> any characters but <c>/</c>,
    /// <c>**</c> any characters, <c>?</c> and <c>+</c> zero-or-one and one-or-more of the preceding character,
    /// <c>[…]</c> one alphanumeric character listed or in an alphanumeric range, <c>\</c> escapes. Anything outside that
    /// cheat sheet is not evaluated.
    /// </summary>
    internal static class FilterPattern
    {
        /// <summary>Whether <paramref name="pattern"/> matches branch <paramref name="branch"/>; null when the pattern
        /// cannot be evaluated with certainty. <paramref name="generous"/>: the pattern makes a workflow run, so it is
        /// matched case-insensitively, also against <c>refs/heads/…</c>, with <c>.</c> as any character; otherwise
        /// strictly.</summary>
        public static bool? Matches(string pattern, string branch, bool generous)
        {
            if (ToRegex(pattern, generous) is not { } regex)
                return null;
            var matcher = new Regex($"\\A(?:{regex})\\z", RegexOptions.CultureInvariant | (generous ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1));
            return matcher.IsMatch(branch) || generous && matcher.IsMatch($"refs/heads/{branch}");
        }

        private static string? ToRegex(string pattern, bool generous)
        {
            if (pattern.Length == 0)
                return null;
            var regex = new StringBuilder();
            var quantifiable = false;
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                switch (c)
                {
                    case '\\':
                        if (++i == pattern.Length)
                            return null;
                        regex.Append(Regex.Escape(pattern[i].ToString()));
                        quantifiable = true;
                        break;
                    case '*':
                        if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                        {
                            regex.Append(".*");
                            i++;
                        }
                        else
                            regex.Append("[^/]*");
                        quantifiable = false;
                        break;
                    case '?' or '+':
                        if (!quantifiable)
                            return null;
                        regex.Append(c);
                        quantifiable = false;
                        break;
                    case '[':
                        var close = pattern.IndexOf(']', i + 1);
                        if (close < 0 || Class(pattern[(i + 1)..close]) is not { } set)
                            return null;
                        regex.Append(set);
                        i = close;
                        quantifiable = true;
                        break;
                    case '.':
                        regex.Append(generous ? "." : "\\.");
                        quantifiable = true;
                        break;
                    case ']' or '(' or ')' or '{' or '}' or '|' or '^' or '$':
                        return null;
                    default:
                        if (char.IsControl(c) || char.IsWhiteSpace(c))
                            return null;
                        regex.Append(Regex.Escape(c.ToString()));
                        quantifiable = true;
                        break;
                }
            }
            return regex.ToString();
        }

        /// <summary><c>[…]</c>: alphanumeric characters and alphanumeric ranges of one kind (<c>a-z</c>, <c>A-Z</c>,
        /// <c>0-9</c>); null for anything else.</summary>
        private static string? Class(string body)
        {
            if (body.Length == 0)
                return null;
            var set = new StringBuilder("[");
            for (var i = 0; i < body.Length; i++)
            {
                if (!char.IsAsciiLetterOrDigit(body[i]))
                    return null;
                if (i + 2 < body.Length && body[i + 1] == '-')
                {
                    var (from, to) = (body[i], body[i + 2]);
                    var sameKind = char.IsAsciiDigit(from) && char.IsAsciiDigit(to) || char.IsAsciiLetterLower(from) && char.IsAsciiLetterLower(to)
                                   || char.IsAsciiLetterUpper(from) && char.IsAsciiLetterUpper(to);
                    if (!sameKind || from > to)
                        return null;
                    set.Append(from).Append('-').Append(to);
                    i += 2;
                }
                else
                    set.Append(body[i]);
            }
            return set.Append(']').ToString();
        }
    }

    // ═══ YAML ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The workflow's root mapping, or null after recording why the queue cannot evaluate the file.</summary>
    private static YamlMappingNode? Load(ForgeWorkflowFile file, string where, List<ForkGuardFinding> findings)
    {
        const string fix = "Keep it a plain YAML workflow of at most 1 MiB directly in `.github/workflows/`.";
        if (file.Content is not { } content)
        {
            findings.Add(new(file.Path, where, null, $"The queue could not read it ({file.Unreadable ?? "no content"}), so it cannot tell when it runs.", fix));
            return null;
        }
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(content));
        }
        catch (YamlException ex)
        {
            findings.Add(new(file.Path, where, Line(ex.Start), $"It does not parse as YAML ({ex.Message}).", fix));
            return null;
        }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            findings.Add(new(file.Path, where, null, "It is not one YAML mapping.", fix));
            return null;
        }
        if (Nodes(root).OfType<YamlMappingNode>().SelectMany(m => m.Children.Keys).FirstOrDefault(k => k is YamlScalarNode { Value: "<<", Style: ScalarStyle.Plain }) is { } merge)
        {
            findings.Add(new(file.Path, where, Line(merge), "It uses a YAML merge key (`<<`), which the queue does not evaluate.", "Write the mapping out in full."));
            return null;
        }
        return root;
    }

    /// <summary>The workflow's triggers (event name in lower case, its configuration or null, the node naming it), or null
    /// after recording why the queue cannot read them.</summary>
    private static List<(string Name, YamlNode? Config, YamlNode Key)>? Triggers(ForgeWorkflowFile file, string where, YamlMappingNode root, List<ForkGuardFinding> findings)
    {
        const string fix = "Write its `on:` as an event, a list of events or a mapping of events.";
        var on = root.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: "on" });
        List<(string, YamlNode?, YamlNode)>? triggers = on.Value switch
        {
            YamlScalarNode { Value: { Length: > 0 } one } scalar when !IsNull(scalar) => [(one.ToLowerInvariant(), null, scalar)],
            YamlSequenceNode many when many.Children.All(c => c is YamlScalarNode { Value: { Length: > 0 } })
                => [.. many.Children.Select(c => (((YamlScalarNode)c).Value!.ToLowerInvariant(), (YamlNode?)null, c))],
            YamlMappingNode map when map.Children.Keys.All(k => k is YamlScalarNode { Value: { Length: > 0 } })
                => [.. map.Children.Select(kv => (((YamlScalarNode)kv.Key).Value!.ToLowerInvariant(), IsNull(kv.Value) ? null : (YamlNode?)kv.Value, kv.Key))],
            _ => null,
        };
        if (triggers is null)
            findings.Add(new(file.Path, where, on.Key is null ? null : Line(on.Key), "The queue cannot read its `on:`.", fix));
        return triggers;
    }

    private static YamlNode? Child(YamlMappingNode map, string key)
        => map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: var k } && k == key).Value;

    /// <summary>A plain empty, <c>~</c> or <c>null</c> scalar, as <c>push:</c> without a value reads.</summary>
    private static bool IsNull(YamlNode? node)
        => node is null || node is YamlScalarNode { Style: ScalarStyle.Plain, Value: null or "" or "~" or "null" or "Null" or "NULL" };

    /// <summary>Every node once, aliases included once: a document of anchors cannot make this explode.</summary>
    private static List<YamlNode> Nodes(YamlNode root)
    {
        var seen = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        var order = new List<YamlNode>();
        var stack = new Stack<YamlNode>([root]);
        while (stack.TryPop(out var node))
        {
            if (!seen.Add(node))
                continue;
            order.Add(node);
            switch (node)
            {
                case YamlMappingNode map:
                    foreach (var (key, value) in map.Children)
                    {
                        stack.Push(value);
                        stack.Push(key);
                    }
                    break;
                case YamlSequenceNode sequence:
                    foreach (var child in sequence.Children)
                        stack.Push(child);
                    break;
            }
        }
        return order;
    }

    private static string Text(YamlNode node) => node is YamlScalarNode { Value: { } value } ? value : node.NodeType.ToString().ToLowerInvariant();

    private static int Line(YamlNode node) => Line(node.Start);

    private static int Line(Mark mark) => (int)Math.Clamp(mark.Line, 1, int.MaxValue);

    /// <summary>A GitHub-hosted runner image label: <c>ubuntu-latest</c>, <c>windows-2022</c>, <c>macos-15-xlarge</c>, …</summary>
    [GeneratedRegex(@"\A(?:ubuntu|windows|macos)-[a-z0-9][a-z0-9.\-]*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubHostedLabel();

    [GeneratedRegex(@"\$\{\{(.*?)\}\}", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Expression();

    [GeneratedRegex(@"(?<![\w.-])secrets\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretsWord();
}
