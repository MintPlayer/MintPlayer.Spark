using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Feedback;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.Onboarding;

/// <summary>What the planner needs besides the tree: the picked features and what the forge said.</summary>
public sealed record OnboardingInputs
{
    public required IReadOnlySet<EOnboardingFeature> Features { get; init; }
    public required string DefaultBranch { get; init; }

    /// <summary>The repository's branches, for the line renames (D2).</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    /// <summary>The commit the dependency action's tag resolves to; null keeps the tag in the workflows.</summary>
    public string? DependencyActionSha { get; init; }

    /// <summary>The commit the versioning action's tag resolves to; null keeps the tag in the step.</summary>
    public string? VersioningActionSha { get; init; }

    public string? VersioningPackageVersion { get; init; }
    public string? Owner { get; init; }

    /// <summary>Whether a package already has a version tag (it then gets no seed).</summary>
    public Func<string, CancellationToken, Task<bool>> IsTagged { get; init; } = (_, _) => Task.FromResult(false);

    /// <summary>D2: the offered older lines (branch names) the owner chose to create; the others are only offered.</summary>
    public IReadOnlyCollection<string> OlderLines { get; init; } = [];
}

/// <param name="Features">What is set up: the picked features, and versioning when release lines are.</param>
/// <param name="Files">The onboarding commit: only what differs from the default branch.</param>
/// <param name="Seeds">The versioning seed tags, pushed at the merge commit.</param>
/// <param name="Renames">The old line branches renamed once the pull request merges (D2).</param>
/// <param name="OnMerge">The repository settings switched on once it merges.</param>
/// <param name="Notes">What the bot could not do itself, for the person merging.</param>
public sealed record OnboardingPlan(IReadOnlyList<EOnboardingFeature> Features, IReadOnlyList<GitWriterFile> Files, IReadOnlyList<VersioningSeed> Seeds,
    LineRenamePlan Renames, IReadOnlyList<EOnboardingFeature> OnMerge, IReadOnlyList<string> Notes, string Body)
{
    /// <summary>The pull request turns on <c>versioning.lines.current: promote</c> (segment T). Ruleset 3 exempts the App
    /// either way (lockfile_notice_and_line_cut_PRD D2).</summary>
    public bool PromotesLines { get; init; }

    /// <summary>
    /// The release lines the bot creates (docs/onboarding_line_cuts_PRD.md): each current line with no branch (D1), and the
    /// older lines the owner chose from <see cref="OfferedLines"/> (D2). Created at the merge, or at once with no files (D3).
    /// </summary>
    public IReadOnlyList<OnboardingLineCut> Cuts { get; init; } = [];

    /// <summary>D2: the older lines a package still targets, with no branch, for the owner to choose from.</summary>
    public IReadOnlyList<OnboardingLineCut> OfferedLines { get; init; } = [];

    /// <summary>
    /// #638 D9: the App's own version tags away from their version's earliest occurrence in the history, for the owner to
    /// move or leave. Filled by the preview, which asks the git writer for a backfill plan.
    /// </summary>
    public IReadOnlyList<PackageVersioning.GitWriterBackfillCollision> TagCollisions { get; init; } = [];
}

/// <summary>
/// The onboarding chooser's one pull request (#571 M10b, coverage_backport_PRD D3 and §8.4 "M10"). Each feature plans over
/// the default branch's tree as the features before it left it (<see cref="OverlayTree"/>), and adds only what is still
/// missing, so planning again on the result changes nothing. That is what lets the bot rebuild the pull request on every
/// new head of the default branch instead of merging into it. coverage.yml sections go in alphabetical order
/// (<see cref="CoverageYmlSections"/>).
/// </summary>
public static partial class OnboardingPlanner
{
    public const string Title = "Set up MintPlayer CodeCoverage";
    public const string Branch = "mintplayer/onboarding";
    private const string Guides = "https://coverage.mintplayer.com";

    /// <summary>D9: the title of a line's pull request that adds the default branch's regenerate workflow.</summary>
    public const string LineTitle = "Add the regenerate workflow to this release line";

    /// <summary>#638 D15: the title of the pull request that narrows an older line to its own .NET major.</summary>
    public const string NarrowTitle = "Narrow this release line to its own .NET major";

    /// <summary>#638 D15: a line's narrowing branch; the git writer names it the same way (<c>DependencyBranches.NarrowingOf</c>).</summary>
    public static string NarrowBranch(string line) => $"mintplayer/narrow-{line}";

    /// <summary>D9: a line's onboarding branch; the git writer names it the same way (<c>DependencyBranches.FeatureOnboardingOf</c>).</summary>
    public static string LineBranch(string line) => $"{Branch}-{line}";

    public static string LineBody(string lineBranch, IEnumerable<string> paths)
    {
        var body = new StringBuilder();
        body.AppendLine($"The merge queue on the default branch regenerates files with these workflows, and `{lineBranch}` does not have them yet:").AppendLine();
        foreach (var path in paths)
            body.AppendLine($"- `{path}`");
        body.AppendLine().AppendLine($"They are copied from the default branch as they are. A backport into `{lineBranch}` runs them on the line itself, so lockfiles and generated files are rebuilt with the line's own tools. Check they build with this line's SDKs before merging. See the [backports guide]({Guides}/backports/setup).");
        return body.ToString();
    }

    /// <summary>The key that turns release lines on, with the default pattern (S3: present, even empty).</summary>
    internal const string LinesKey = "lines: {}                          # release lines, release/{ecosystem}-{major}.x: packages release only from them";

    /// <summary>
    /// Segment T: release lines whose current lines are promoted by <c>/release</c> (the default, owner delegated
    /// 2026-11-11), waiting for <paramref name="workflow"/>, the push workflow that runs the versioning step.
    /// </summary>
    internal static string PromoteLinesKey(string workflow) => $"""
        lines:                             # release lines, release/{"{ecosystem}"}-{"{major}"}.x: packages release only from them
          current: promote                 # a maintainer's `/release` moves each current line to the newest commit {workflow} passed
          trigger: manual
          workflow: {workflow}
        """;

    public static async Task<OnboardingPlan> PlanAsync(IRepositoryTree tree, OnboardingInputs inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(inputs);
        var features = inputs.Features.ToHashSet();
        if (features.Contains(EOnboardingFeature.ReleaseLines))
            features.Add(EOnboardingFeature.Versioning);

        var overlay = new OverlayTree(tree);
        var notes = new List<string>();
        var sections = new List<(string Heading, string Text)>();
        IReadOnlyList<VersioningSeed> seeds = [];
        var renames = new LineRenamePlan([], []);
        var promotes = false;
        IReadOnlyList<OnboardingLineCut> cuts = [], offered = [];

        if (features.Contains(EOnboardingFeature.Versioning))
        {
            var versioning = await VersioningOnboarding.PlanAsync(overlay, inputs.IsTagged, inputs.VersioningActionSha, inputs.VersioningPackageVersion, inputs.Owner,
                cancellationToken);
            foreach (var file in versioning.Files)
                overlay.Apply(file);
            seeds = versioning.Seeds;
            sections.Add(("Versioning", versioning.Body));
        }
        if (features.Contains(EOnboardingFeature.ReleaseLines))
        {
            (renames, promotes, var triggers, var current, offered) = await ReleaseLinesAsync(overlay, inputs, notes, cancellationToken);
            var chosen = inputs.OlderLines.ToHashSet(StringComparer.Ordinal);
            cuts = [.. current, .. offered.Where(o => chosen.Contains(o.Branch))];
            sections.Add(("Release lines", ReleaseLinesText(renames, inputs.DefaultBranch, promotes, triggers, cuts, offered.Where(o => !chosen.Contains(o.Branch)).ToList())));
        }
        if (features.Contains(EOnboardingFeature.MergeQueue))
            sections.Add(("Merge queue", await MergeQueueAsync(overlay, inputs.DefaultBranch, features.Contains(EOnboardingFeature.ReleaseLines), notes, cancellationToken)));
        if (features.Contains(EOnboardingFeature.DependencyUpdates))
            sections.Add(("Dependency updates", await DependencyUpdatesAsync(overlay, inputs, cancellationToken)));
        if (features.Contains(EOnboardingFeature.Coverage))
            sections.Add(("Coverage",
                $"- [ ] Upload coverage from your build: add the step for your stack from the [coverage guide]({Guides}/coverage/setup). "
                + "The bot does not write it for you, because where it goes depends on how your workflows build and test."));
        if (features.Contains(EOnboardingFeature.SizeLabels))
            sections.Add(("PR size labels", "Switched on for this repository when this pull request merges: every pull request gets one label, `size/XS` to `size/XXL`."));

        var ordered = Enum.GetValues<EOnboardingFeature>().Where(features.Contains).ToList();
        var onMerge = ordered.Where(f => f is EOnboardingFeature.DependencyUpdates or EOnboardingFeature.SizeLabels).ToList();
        return new OnboardingPlan(ordered, await overlay.ChangesAsync(cancellationToken), seeds, renames, onMerge, notes,
            Body(ordered, sections, notes, seeds, renames, cuts, onMerge, inputs.DefaultBranch)) { PromotesLines = promotes, Cuts = cuts, OfferedLines = offered };
    }

    /// <summary>
    /// Adds <c>versioning.lines</c> when coverage.yml has none (an existing one is left as it is). Segment T: with exactly
    /// one push workflow running the versioning step, the current lines are promoted with <c>/release</c> and wait for that
    /// workflow; with none or several the bot cannot tell which one proves a commit, so the lines take backports and a
    /// note says how to switch.
    /// </summary>
    private static async Task<(LineRenamePlan Renames, bool Promotes, IReadOnlyList<string> Triggers, IReadOnlyList<OnboardingLineCut> Current,
        IReadOnlyList<OnboardingLineCut> Offered)> ReleaseLinesAsync(OverlayTree overlay, OnboardingInputs inputs, List<string> notes,
        CancellationToken cancellationToken)
    {
        var yml = await overlay.ReadTextAsync(CoverageYml.FileName, Limits.MaxManifestBytes, cancellationToken);
        var config = VersioningYml.Parse(yml).Config;
        var pattern = config?.EffectiveLines;
        var lines = pattern;
        var promotes = config?.Lines?.PromotesCurrent == true;
        if (pattern is null)
        {
            var workflows = await VersioningOnboarding.VersioningWorkflowsAsync(overlay, cancellationToken);
            var key = workflows.Count == 1 ? PromoteLinesKey(workflows[0]) : LinesKey;
            if (CoverageYmlSections.AddKey(yml ?? string.Empty, "versioning", "lines", key) is { } edited)
            {
                overlay.Write(CoverageYml.FileName, edited);
                lines = ReleaseLinePattern.Default;
                promotes = workflows.Count == 1;
                if (!promotes)
                    notes.Add(workflows.Count == 0
                        ? "No push workflow runs the versioning step, so the release lines take backports: once one does, set `versioning.lines` to `{ current: promote, trigger: manual, workflow: <its file> }` to release them with `/release` instead."
                        : $"Several push workflows run the versioning step ({string.Join(", ", workflows.Select(w => $"`{w}`"))}), so the release lines take backports: set `versioning.lines` to `{{ current: promote, trigger: manual, workflow: <the one whose green run proves a commit> }}` to release them with `/release` instead.");
            }
            else
                notes.Add("`versioning:` in coverage.yml is not a block the bot can extend, so it did not add `versioning.lines`: add `lines: {}` under `versioning:` to turn release lines on.");
        }
        // Gap G2 (SparkTest trial, plan §2d): an existing `versioning.lines` stays as it is (T6), but a repository that could
        // promote is told so, since nothing else would.
        else if (!promotes && await VersioningOnboarding.VersioningWorkflowsAsync(overlay, cancellationToken) is [var only])
            notes.Add($"coverage.yml has `versioning.lines` already, so the bot left it alone and the current lines take backports: set it to `{{ current: promote, trigger: manual, workflow: {only} }}` to release them with `/release` instead.");

        IReadOnlyList<string> triggers = lines is null ? [] : await TriggersAsync(overlay, lines, inputs.DefaultBranch, notes, cancellationToken);

        var graph = await ProjectGraphBuilder.BuildAsync(overlay, cancellationToken: cancellationToken);
        var ecosystems = graph.Packages.Select(p => Ecosystems.Of(graph, p, EmptyPins)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var renames = LineRenames.Plan(inputs.Branches, inputs.DefaultBranch, ecosystems, pattern ?? ReleaseLinePattern.Default);
        if (lines is null) return (renames, promotes, triggers, [], []);

        // docs/onboarding_line_cuts_PRD.md D1, D2: the lines with no branch once the renames ran.
        var settings = VersioningYml.Parse(await overlay.ReadTextAsync(CoverageYml.FileName, Limits.MaxManifestBytes, cancellationToken)).Config?.ToSettings()
            ?? new VersioningSettings();
        var branches = inputs.Branches.Except(renames.Renames.Select(r => r.From), StringComparer.Ordinal).Concat(renames.Renames.Select(r => r.To)).ToList();
        var (current, offered) = OnboardingLineCuts.Plan(graph, settings, lines, branches);
        return (renames, promotes, triggers, current, offered);
    }

    /// <summary>
    /// The Tools onboarding's gap (2026-11-12): lines publish, so the versioning workflows' <c>push</c> filters and every
    /// <c>pull_request</c> filter that names the default branch take the lines too (<see cref="ReleaseLineTriggers"/>).
    /// The workflows it edited, sorted.
    /// </summary>
    private static async Task<IReadOnlyList<string>> TriggersAsync(OverlayTree overlay, ReleaseLinePattern lines, string defaultBranch, List<string> notes,
        CancellationToken cancellationToken)
    {
        var versioning = (await VersioningOnboarding.VersioningWorkflowsAsync(overlay, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var edited = new List<string>();
        foreach (var path in overlay.Entries.Keys.Where(IsWorkflow).Order(StringComparer.Ordinal).ToList())
        {
            if (await overlay.ReadTextAsync(path, Limits.MaxManifestBytes, cancellationToken) is not { } text) continue;
            var push = versioning.Contains(path[WorkflowsFolder.Length..]);
            if (ReleaseLineTriggers.Add(path, text, lines, defaultBranch, push, notes) is { } changed)
            {
                overlay.Write(path, changed);
                edited.Add(path);
            }
        }
        return edited;
    }

    private const string WorkflowsFolder = ".github/workflows/";

    private static bool IsWorkflow(string path)
        => path.StartsWith(WorkflowsFolder, StringComparison.Ordinal) && path.IndexOf('/', WorkflowsFolder.Length) < 0
            && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal));

    private static readonly IReadOnlyDictionary<string, string> EmptyPins = new Dictionary<string, string>(StringComparer.Ordinal);

    private static string ReleaseLinesText(LineRenamePlan renames, string defaultBranch, bool promotes, IReadOnlyList<string> triggers,
        IReadOnlyList<OnboardingLineCut> cuts, IReadOnlyList<OnboardingLineCut> declined)
    {
        var text = new StringBuilder();
        text.AppendLine(promotes
            ? $"`versioning.lines` turns release lines on. From then on, packages release only from their line (`release/<ecosystem>-<major>.x`), never from `{defaultBranch}`. The current line of each platform moves when a maintainer comments `/release`, to the newest `{defaultBranch}` commit whose workflow passed; older lines take backport pull requests. See the [backports guide]({Guides}/backports/setup)."
            : $"`versioning.lines` turns release lines on. From then on, packages release only from their line (`release/<ecosystem>-<major>.x`), never from `{defaultBranch}`, and changes reach a line by backport pull request. See the [backports guide]({Guides}/backports/setup).");
        if (triggers.Count > 0)
            text.AppendLine().AppendLine($"These workflows now run on the lines too, so the lines publish and their pull requests get checked: {string.Join(", ", triggers.Select(t => $"`{t}`"))}.");
        if (renames.Renames.Count > 0)
        {
            text.AppendLine().AppendLine("These branches are renamed when this pull request merges. GitHub moves their open pull requests along, but `git fetch` does not follow the old names: switch your clones to the new branches. The renames:").AppendLine();
            text.AppendLine("| Branch now | Renamed to |").AppendLine("|---|---|");
            foreach (var rename in renames.Renames)
                text.AppendLine($"| `{rename.From}` | `{rename.To}` |");
        }
        if (renames.Skipped.Count > 0)
        {
            text.AppendLine().AppendLine("Not renamed:");
            foreach (var skip in renames.Skipped)
                text.AppendLine($"- `{skip.Branch}`: {skip.Reason}.");
        }
        if (cuts.Count > 0)
        {
            text.AppendLine().AppendLine($"The bot creates these lines from the merge commit, since no person may push a new line (only the App passes its required checks):").AppendLine();
            foreach (var cut in cuts)
                text.AppendLine($"- `{cut.Branch}` ({cut.Reason}): {string.Join(", ", cut.Packages.Select(p => $"`{p}`"))}.");
            // #638 D15: an older line starts out targeting the higher major too.
            if (cuts.Any(c => c.TargetFramework is not null))
                text.AppendLine().AppendLine("An older line is cut from a commit that also targets a higher major. Once it exists, the bot opens a pull request into it "
                    + "that drops the higher target frameworks, moves `global.json` and the framework packages to the line's major, and lists the "
                    + "`#if NETxx_OR_GREATER` and framework conditions for you to resolve.");
        }
        if (declined.Count > 0)
        {
            text.AppendLine().AppendLine("Not created, as chosen (run **Set up features** again to create them):");
            foreach (var line in declined)
                text.AppendLine($"- `{line.Branch}`: {line.Reason}.");
        }
        return text.ToString();
    }

    private static async Task<string> MergeQueueAsync(OverlayTree overlay, string defaultBranch, bool lines, List<string> notes, CancellationToken cancellationToken)
    {
        var yml = await overlay.ReadTextAsync(CoverageYml.FileName, Limits.MaxManifestBytes, cancellationToken);
        if (!CoverageYmlSections.Has(yml, "mergeQueue"))
        {
            var section = lines
                ? DependencyOnboarding.MergeQueueSection.Replace("  enabled: true\n", $"  enabled: true\n  branches: [{FlowScalar(defaultBranch)}, supported-lines]\n", StringComparison.Ordinal)
                : DependencyOnboarding.MergeQueueSection;
            overlay.Write(CoverageYml.FileName, CoverageYmlSections.Add(yml, "mergeQueue", section));
            return lines
                ? $"`mergeQueue:` queues `{defaultBranch}` and the supported release lines, so backports and the lines' dependency pull requests merge through the queue. See the [merge queue guide]({Guides}/merge-queue/setup)."
                : $"`mergeQueue:` is enabled for `{defaultBranch}`. Label a pull request `merge-queue`, or comment `/queue`. See the [merge queue guide]({Guides}/merge-queue/setup).";
        }
        if (lines && yml?.Contains("supported-lines", StringComparison.Ordinal) != true)
        {
            // The Tools onboarding's gap (2026-11-12): a section without `branches` queues the default branch only, so it
            // gets the key; one with its own list is the owner's, and only a note asks for the lines.
            var branches = $"branches: [{FlowScalar(defaultBranch)}, supported-lines]";
            if (CoverageYmlSections.AddKey(yml!, "mergeQueue", "branches", branches) is { } edited && edited != yml)
            {
                overlay.Write(CoverageYml.FileName, edited);
                return $"coverage.yml has a `mergeQueue:` section already; it now queues the supported release lines too (`{branches}`), so backports merge through the queue.";
            }
            notes.Add("coverage.yml has a `mergeQueue:` section already, so the bot left it alone: add `supported-lines` to `mergeQueue.branches` so backports reach the lines through the queue.");
        }
        return "coverage.yml has a `mergeQueue:` section already; the bot left it as it is.";
    }

    private static async Task<string> DependencyUpdatesAsync(OverlayTree overlay, OnboardingInputs inputs, CancellationToken cancellationToken)
    {
        var yml = await overlay.ReadTextAsync(CoverageYml.FileName, Limits.MaxManifestBytes, cancellationToken);
        string? dependabotPath = null, dependabotYml = null;
        foreach (var path in new[] { ".github/dependabot.yml", ".github/dependabot.yaml" })
        {
            if (await overlay.ReadTextAsync(path, Limits.MaxManifestBytes, cancellationToken) is { } text)
            {
                (dependabotPath, dependabotYml) = (path, text);
                break;
            }
        }
        var plan = DependencyOnboarding.Plan([.. overlay.Entries.Keys], yml, dependabotPath, dependabotYml, inputs.DependencyActionSha, DependencyOnboarding.ReadTemplate,
            addMergeQueue: false);
        foreach (var file in plan.Files)
            overlay.Apply(file);
        return plan.Body;
    }

    private static string Body(IReadOnlyList<EOnboardingFeature> features, List<(string Heading, string Text)> sections, List<string> notes,
        IReadOnlyList<VersioningSeed> seeds, LineRenamePlan renames, IReadOnlyList<OnboardingLineCut> cuts, IReadOnlyList<EOnboardingFeature> onMerge, string defaultBranch)
    {
        var body = new StringBuilder();
        body.AppendLine($"This pull request sets up {string.Join(", ", features.Select(Name))} for this repository, as chosen on its CodeCoverage page.").AppendLine();
        foreach (var (heading, text) in sections)
            body.AppendLine($"## {heading}").AppendLine().AppendLine(text.Trim()).AppendLine();
        if (notes.Count > 0)
        {
            body.AppendLine("## To do by hand").AppendLine();
            foreach (var note in notes)
                body.AppendLine($"- [ ] {note}");
            body.AppendLine();
        }

        body.AppendLine("## When this merges").AppendLine();
        foreach (var feature in onMerge)
            body.AppendLine($"- {Name(feature)} is switched on for the repository.");
        if (seeds.Count > 0)
            body.AppendLine($"- {seeds.Count} seed tag(s) are pushed at the merge commit, so each package continues from the version it has now.");
        if (renames.Renames.Count > 0)
            body.AppendLine($"- {renames.Renames.Count} line branch(es) are renamed, as listed above.");
        if (cuts.Count > 0)
            body.AppendLine($"- {cuts.Count} release line(s) are created from the merge commit, as listed above.");
        if (features.Contains(EOnboardingFeature.ReleaseLines))
            body.AppendLine("- Each release line without the default branch's regenerate workflow gets a pull request that adds it, so backports can regenerate lockfiles with the line's own tools.");
        body.AppendLine().AppendLine($"The bot rebuilds this pull request whenever `{defaultBranch}` moves, so it never goes stale or conflicts. Don't push to its branch: a rebuild replaces your commits. To change what it sets up, run **Set up features** again.");
        return body.ToString();
    }

    private static string Name(EOnboardingFeature feature) => feature switch
    {
        EOnboardingFeature.Coverage => "coverage",
        EOnboardingFeature.DependencyUpdates => "dependency updates",
        EOnboardingFeature.MergeQueue => "the merge queue",
        EOnboardingFeature.ReleaseLines => "release lines",
        EOnboardingFeature.SizeLabels => "PR size labels",
        EOnboardingFeature.Versioning => "package versioning",
        _ => feature.ToString(),
    };

    /// <summary>A branch name as a YAML flow-sequence item: plain when it is safe, else double-quoted.</summary>
    private static string FlowScalar(string branch) => PlainBranch().IsMatch(branch) ? branch : "\"" + branch.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex PlainBranch();
}
