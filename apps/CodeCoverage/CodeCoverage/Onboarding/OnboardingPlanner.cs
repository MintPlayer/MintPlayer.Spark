using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Feedback;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.Onboarding;

/// <summary>What the onboarding chooser sets up (#568 D3). Release lines need versioning, and bring it with them.</summary>
public enum EOnboardingFeature
{
    Coverage,
    DependencyUpdates,
    MergeQueue,
    ReleaseLines,
    SizeLabels,
    Versioning,
}

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
}

/// <param name="Features">What is set up: the picked features, and versioning when release lines are.</param>
/// <param name="Files">The onboarding commit: only what differs from the default branch.</param>
/// <param name="Seeds">The versioning seed tags, pushed at the merge commit.</param>
/// <param name="Renames">The old line branches renamed once the pull request merges (D2).</param>
/// <param name="OnMerge">The repository settings switched on once it merges.</param>
/// <param name="Notes">What the bot could not do itself, for the person merging.</param>
public sealed record OnboardingPlan(IReadOnlyList<EOnboardingFeature> Features, IReadOnlyList<GitWriterFile> Files, IReadOnlyList<VersioningSeed> Seeds,
    LineRenamePlan Renames, IReadOnlyList<EOnboardingFeature> OnMerge, IReadOnlyList<string> Notes, string Body);

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

    /// <summary>The key that turns release lines on, with the default pattern (S3: present, even empty).</summary>
    internal const string LinesKey = "lines: {}                          # release lines, release/{ecosystem}-{major}.x: packages release only from them";

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
            renames = await ReleaseLinesAsync(overlay, inputs, notes, cancellationToken);
            sections.Add(("Release lines", ReleaseLinesText(renames, inputs.DefaultBranch)));
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
            Body(ordered, sections, notes, seeds, renames, onMerge, inputs.DefaultBranch));
    }

    private static async Task<LineRenamePlan> ReleaseLinesAsync(OverlayTree overlay, OnboardingInputs inputs, List<string> notes, CancellationToken cancellationToken)
    {
        var yml = await overlay.ReadTextAsync(CoverageYml.FileName, Limits.MaxManifestBytes, cancellationToken);
        var pattern = VersioningYml.Parse(yml).Config?.EffectiveLines;
        if (pattern is null)
        {
            if (CoverageYmlSections.AddKey(yml ?? string.Empty, "versioning", "lines", LinesKey) is { } edited)
                overlay.Write(CoverageYml.FileName, edited);
            else
                notes.Add("`versioning:` in coverage.yml is not a block the bot can extend, so it did not add `versioning.lines`: add `lines: {}` under `versioning:` to turn release lines on.");
        }

        var graph = await ProjectGraphBuilder.BuildAsync(overlay, cancellationToken: cancellationToken);
        var ecosystems = graph.Packages.Select(p => Ecosystems.Of(graph, p, EmptyPins)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return LineRenames.Plan(inputs.Branches, inputs.DefaultBranch, ecosystems, pattern ?? ReleaseLinePattern.Default);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyPins = new Dictionary<string, string>(StringComparer.Ordinal);

    private static string ReleaseLinesText(LineRenamePlan renames, string defaultBranch)
    {
        var text = new StringBuilder();
        text.AppendLine($"`versioning.lines` turns release lines on. From then on, packages release only from their line (`release/<ecosystem>-<major>.x`), never from `{defaultBranch}`, and changes reach a line by backport pull request. See the [backports guide]({Guides}/backports/setup).");
        if (renames.Renames.Count > 0)
        {
            text.AppendLine().AppendLine("These branches are renamed when this pull request merges. GitHub moves their open pull requests along and redirects the old names:").AppendLine();
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
            notes.Add("coverage.yml has a `mergeQueue:` section already, so the bot left it alone: add `supported-lines` to `mergeQueue.branches` so backports reach the lines through the queue.");
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
        IReadOnlyList<VersioningSeed> seeds, LineRenamePlan renames, IReadOnlyList<EOnboardingFeature> onMerge, string defaultBranch)
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
