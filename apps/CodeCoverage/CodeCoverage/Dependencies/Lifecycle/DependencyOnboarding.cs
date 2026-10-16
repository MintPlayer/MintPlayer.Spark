using System.Text;
using CodeCoverage.MergeQueue;
using YamlDotNet.RepresentationModel;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>What the onboarding pull request writes, and what its body says (R16, §7.2).</summary>
/// <param name="Files">The onboarding commit: each path's new text, or null to delete it.</param>
/// <param name="TranslatedRules">Dependabot's removed <c>ignore:</c> entries as suggested <c>coverage.yml</c> lines.</param>
/// <param name="RemovedEcosystems">The <c>updates:</c> entries taken out of <c>dependabot.yml</c>.</param>
public sealed record DependencyOnboardingPlan(IReadOnlyList<GitWriterFile> Files, IReadOnlyList<string> TranslatedRules, IReadOnlyList<string> RemovedEcosystems,
    IReadOnlyList<string> Registries, bool Pinned, string Body);

/// <summary>
/// R16: the onboarding pull request switches the repository over. It adds thin callers of the versioned action (the shipped
/// templates, the action's <c>@dependency-update-v1</c> pinned to the commit the bot resolved), a commented <c>dependencies:</c>
/// section in <c>coverage.yml</c>, and removes <c>dependabot.yml</c>'s version-update entries, whose <c>ignore:</c> rules it
/// translates into the body. Dependabot alerts and security updates are repository settings and stay as they are.
/// </summary>
public static class DependencyOnboarding
{
    public const string ActionReference = "MintPlayer/MintPlayer.Spark/apps/CodeCoverage/dependency-action@dependency-update-v1";
    public const string ActionRepository = "MintPlayer/MintPlayer.Spark";
    public const string ActionTag = "dependency-update-v1";

    public const string Title = "Set up MintPlayer dependency updates";

    /// <summary>The section appended to <c>coverage.yml</c>, all commented: the defaults apply until someone uncomments it.</summary>
    public const string CommentedSection = """

        # MintPlayer dependency updates (added by the onboarding pull request). Every line is a default: uncomment to change it.
        # dependencies:
        #   enabled: true
        #   schedule: { frequency: weekly, weekday: monday, hour: 6 }   # in the account's time zone
        #   cooldown: 3d                     # third-party releases wait this long; may only lengthen the account's
        #   autoMerge:                       # the largest update merged through the merge queue without an approval
        #     default: minor
        #     "@angular/*": never            # never: every update waits for `approve <group>`
        #     maxCoverageDrop: 0.5           # percentage points
        #   rules:
        #     - match: ["some-package"]
        #       ignore: true
        #   migrate:                         # approved majors run a migration workflow after the apply
        #     - match: "@angular/core"
        #       onMajor: angular-migrate.yml
        #     - match: "nx"
        #       onMajor: nx-migrate.yml
        """;

    /// <summary>R25: what the onboarding pull request adds to <c>coverage.yml</c> when its setting resolves to Yes.</summary>
    public const string MergeQueueSection = """
        # The merge queue (added by the onboarding pull request): dependency pull requests within your thresholds merge through
        # it, and your own pull requests can use it too (label `merge-queue`, or comment `/queue`).
        mergeQueue:
          enabled: true

        """;

    /// <summary>Whether <paramref name="yml"/> has <paramref name="key"/> as a top-level key (not commented, not indented).</summary>
    public static bool HasTopLevelKey(string? yml, string key)
        => yml is not null && yml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Any(line => line.StartsWith(key, StringComparison.Ordinal) && line.AsSpan(key.Length).TrimStart().StartsWith(":"));

    /// <summary>The workflow templates, by repository path; the migrate ones only where their tool is used.</summary>
    public static IReadOnlyList<(string Path, string Resource)> Templates(IReadOnlyCollection<string> treePaths)
    {
        var list = new List<(string, string)>
        {
            (".github/workflows/dependency-lookup.yml", "dependency-lookup.yml"),
            (".github/workflows/dependency-apply.yml", "dependency-apply.yml"),
        };
        if (treePaths.Any(p => p == "angular.json" || p.EndsWith("/angular.json", StringComparison.Ordinal)))
            list.Add((".github/workflows/angular-migrate.yml", "angular-migrate.yml"));
        if (treePaths.Any(p => p == "nx.json" || p.EndsWith("/nx.json", StringComparison.Ordinal)))
            list.Add((".github/workflows/nx-migrate.yml", "nx-migrate.yml"));
        return list;
    }

    public static string ReadTemplate(string resource)
    {
        var name = $"DependencyTemplates.{resource}";
        using var stream = typeof(DependencyOnboarding).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded workflow template '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The template with the action pinned to <paramref name="actionSha"/> (the tag kept as a comment), or unchanged
    /// when the bot could not resolve the tag.</summary>
    public static string Pin(string template, string? actionSha)
        => actionSha is { Length: 40 }
            ? template.Replace(ActionReference, $"{ActionReference[..ActionReference.LastIndexOf('@')]}@{actionSha} # {ActionTag}", StringComparison.Ordinal)
            : template;

    public static DependencyOnboardingPlan Plan(IReadOnlyCollection<string> treePaths, string? coverageYml, string? dependabotPath, string? dependabotYml,
        string? actionSha, Func<string, string> readTemplate, bool addMergeQueue = false)
    {
        var files = new List<GitWriterFile>();
        foreach (var (path, resource) in Templates(treePaths))
            files.Add(new GitWriterFile(path, Pin(readTemplate(resource), actionSha)));

        var yml = coverageYml ?? string.Empty;
        var text = yml.Length == 0 || yml.EndsWith('\n') ? yml : yml + "\n";
        // R25: the merge queue is what R5 auto-merges through. Added only when the file has no mergeQueue section at all:
        // one that is there, enabled or not, is the owner's choice.
        if (addMergeQueue && !HasTopLevelKey(yml, "mergeQueue"))
            text += MergeQueueSection;
        if (!yml.Contains("\ndependencies:", StringComparison.Ordinal) && !yml.StartsWith("dependencies:", StringComparison.Ordinal)
            && !yml.Contains("# dependencies:", StringComparison.Ordinal))
            text += CommentedSection.TrimStart('\r', '\n') + "\n";
        if (text != yml && text != yml + "\n")
            files.Add(new GitWriterFile("coverage.yml", text));

        var (newDependabot, removed, rules, registries) = TranslateDependabot(dependabotYml);
        if (dependabotPath is not null && removed.Count > 0)
            files.Add(new GitWriterFile(dependabotPath, newDependabot, Delete: newDependabot is null));

        return new DependencyOnboardingPlan(files, rules, removed, registries, actionSha is { Length: 40 },
            Body(files, rules, removed, registries, actionSha, dependabotPath, addMergeQueue && !HasTopLevelKey(yml, "mergeQueue")));
    }

    /// <summary>
    /// <c>dependabot.yml</c> without its <c>updates:</c> (version updates, every ecosystem: R12 covers whatever the lookup reports),
    /// null when nothing else remains; the removed ecosystems, the <c>ignore:</c> entries as <c>coverage.yml</c> rule lines, and the
    /// <c>registries:</c> hosts. A file that is not YAML is left alone.
    /// </summary>
    public static (string? Yml, IReadOnlyList<string> Removed, IReadOnlyList<string> Rules, IReadOnlyList<string> Registries) TranslateDependabot(string? yml)
    {
        if (string.IsNullOrWhiteSpace(yml))
            return (null, [], [], []);
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
                return (yml, [], [], []);
            root = mapping;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return (yml, [], [], []);
        }

        var removed = new List<string>();
        var rules = new List<string>();
        var registries = new List<string>();
        if (root.Children.TryGetValue(new YamlScalarNode("updates"), out var updates) && updates is YamlSequenceNode entries)
        {
            foreach (var entry in entries.Children.OfType<YamlMappingNode>())
            {
                var ecosystem = Scalar(entry, "package-ecosystem") ?? "?";
                var directory = Scalar(entry, "directory")
                    ?? (entry.Children.TryGetValue(new YamlScalarNode("directories"), out var dirs) && dirs is YamlSequenceNode list
                        ? string.Join(", ", list.Children.OfType<YamlScalarNode>().Select(d => d.Value))
                        : "/");
                removed.Add($"{ecosystem} ({directory})");
                if (entry.Children.TryGetValue(new YamlScalarNode("ignore"), out var ignore) && ignore is YamlSequenceNode ignores)
                {
                    foreach (var rule in ignores.Children.OfType<YamlMappingNode>())
                        rules.AddRange(TranslateIgnore(rule));
                }
            }
            root.Children.Remove(new YamlScalarNode("updates"));
        }
        if (root.Children.TryGetValue(new YamlScalarNode("registries"), out var registryNode) && registryNode is YamlMappingNode registryMap)
        {
            foreach (var (name, value) in registryMap.Children)
            {
                if (value is YamlMappingNode registry && Scalar(registry, "url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    registries.Add($"{((YamlScalarNode)name).Value}: {uri.Host}");
            }
        }

        // What only served the version updates goes with them: the version line, the registries and the groups of updates.
        var meaningful = root.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value)
            .Where(k => k is not ("version" or "registries" or "multi-ecosystem-groups" or "groups")).ToList();
        if (removed.Count == 0)
            return (yml, [], rules, registries);
        if (meaningful.Count == 0)
            return (null, removed, rules, registries);
        var doc = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        doc.Save(writer, assignAnchors: false);
        return (writer.ToString().Replace("...\r\n", string.Empty).Replace("...\n", string.Empty), removed, rules, registries);
    }

    /// <summary>One Dependabot ignore entry as rule lines: no versions or update types → <c>ignore: true</c>; majors only →
    /// a <c>minor</c> threshold (the major then waits for an approval, R6); a version list → a note, since rules take no
    /// ranges (R1).</summary>
    private static IEnumerable<string> TranslateIgnore(YamlMappingNode rule)
    {
        if (Scalar(rule, "dependency-name") is not { } name)
            yield break;
        var types = rule.Children.TryGetValue(new YamlScalarNode("update-types"), out var t) && t is YamlSequenceNode s
            ? s.Children.OfType<YamlScalarNode>().Select(n => n.Value).OfType<string>().ToList()
            : [];
        var hasVersions = rule.Children.ContainsKey(new YamlScalarNode("versions"));
        var quoted = $"\"{name.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        if (types.Count == 0 && !hasVersions)
            yield return $"{RulePrefix}- {{ match: [{quoted}], ignore: true }}";
        else if (types.Count > 0 && !hasVersions && types.All(x => x == "version-update:semver-major"))
            yield return $"{AutoMergePrefix}{quoted}: minor   # majors wait for `approve`";
        else if (types.Count > 0 && !hasVersions && types.All(x => x is "version-update:semver-major" or "version-update:semver-minor"))
            yield return $"{AutoMergePrefix}{quoted}: patch   # minors and majors wait for `approve`";
        else
            yield return $"# {name}: Dependabot ignored some versions; rules take no version ranges, so hold it (hold: true) or approve by hand";
    }

    /// <summary>A translated line that belongs under <c>dependencies.rules</c>.</summary>
    public const string RulePrefix = "rules|";

    /// <summary>A translated line that belongs under <c>dependencies.autoMerge</c>.</summary>
    public const string AutoMergePrefix = "autoMerge|";

    /// <summary>The translated lines as one pasteable <c>dependencies:</c> block.</summary>
    public static string RulesYaml(IReadOnlyList<string> rules)
    {
        var yaml = new StringBuilder("dependencies:\n");
        var ruleLines = rules.Where(r => r.StartsWith(RulePrefix, StringComparison.Ordinal)).Select(r => r[RulePrefix.Length..]).ToList();
        var mergeLines = rules.Where(r => r.StartsWith(AutoMergePrefix, StringComparison.Ordinal)).Select(r => r[AutoMergePrefix.Length..]).ToList();
        if (ruleLines.Count > 0)
            yaml.Append("  rules:\n").AppendJoin("", ruleLines.Select(l => $"    {l}\n"));
        if (mergeLines.Count > 0)
            yaml.Append("  autoMerge:\n").AppendJoin("", mergeLines.Select(l => $"    {l}\n"));
        foreach (var note in rules.Where(r => r.StartsWith('#')))
            yaml.Append("  ").Append(note).Append('\n');
        return yaml.ToString();
    }

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;

    private static string Body(IReadOnlyList<GitWriterFile> files, IReadOnlyList<string> rules, IReadOnlyList<string> removed, IReadOnlyList<string> registries,
        string? actionSha, string? dependabotPath, bool mergeQueueAdded = false)
    {
        var body = new StringBuilder();
        body.AppendLine(DependencyPullRequestBody.Marker);
        body.AppendLine("This switches the repository to MintPlayer dependency updates: one rolling pull request, rebuilt on your schedule, merged through the merge queue when it is within your thresholds and green.").AppendLine();
        body.AppendLine("## What this adds").AppendLine();
        foreach (var file in files)
            body.AppendLine($"- `{file.Path}`{(file.Delete ? " (deleted)" : string.Empty)}");
        body.AppendLine();
        if (mergeQueueAdded)
            body.AppendLine("`coverage.yml` gets `mergeQueue: { enabled: true }`: dependency pull requests within your thresholds merge through the merge queue, and your own pull requests can use it too. See the [merge queue guide](https://coverage.mintplayer.com/merge-queue/setup). Remove the section to keep merging by hand.").AppendLine();
        body.AppendLine(actionSha is { Length: 40 }
            ? $"The workflows call the action pinned to `{actionSha}` (`{ActionTag}`); the bot keeps that pin current through its own Actions updates."
            : $"The workflows call the action at `{ActionTag}`; pin it to a commit if your policy requires it.");
        body.AppendLine();
        body.AppendLine("Both workflows run with `contents: read`, no `id-token` and `persist-credentials: false`. They never push: the bot validates their output and its git writer pushes.").AppendLine();

        if (removed.Count > 0)
        {
            body.AppendLine("## Dependabot").AppendLine();
            body.AppendLine($"`{dependabotPath}` loses its version-update entries ({string.Join(", ", removed)}). Dependabot **alerts** and security updates are repository settings and stay as they are.").AppendLine();
        }
        if (rules.Count > 0)
        {
            body.AppendLine("Its `ignore:` rules, translated for `dependencies:` in coverage.yml (add the ones you want to keep):").AppendLine();
            body.AppendLine("```yaml");
            body.Append(RulesYaml(rules));
            body.AppendLine("```").AppendLine();
        }
        if (registries.Count > 0)
        {
            body.AppendLine("Private registries it named (give the lookup workflow a `host-rules` secret for them, and list the hosts under `dependencies.registries`):").AppendLine();
            foreach (var registry in registries)
                body.AppendLine($"- {registry}");
            body.AppendLine();
        }
        body.AppendLine("## Permissions").AppendLine();
        body.AppendLine("The App needs **Workflows: write** on this repository to open this pull request and to bump Actions `uses:` lines. If an installation has not accepted it yet, GitHub shows an accept-new-permissions prompt to its owner.").AppendLine();
        body.AppendLine("After this merges, the first rolling pull request is opened as a draft.");
        return body.ToString();
    }
}
