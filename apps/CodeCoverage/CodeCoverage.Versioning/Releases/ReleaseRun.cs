using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>One run's inputs beside the repository: the manifest, the repository's settings and the branch's line (D10).</summary>
/// <param name="Run">The workflow run's page, written into every tag (#544 D5); null writes none.</param>
public sealed record ReleaseRunRequest(ReleaseManifest Manifest, VersioningSettings Settings, bool OnDefaultBranch, int? BranchMajor, string? Run = null);

/// <summary>What a run planned and what became of each tag. <see cref="Tags"/> is empty when the plan refused anything.</summary>
/// <param name="Notes">Per tag name, the Release text (D15) of each tag the run created or found already in place.</param>
/// <param name="BranchCuts">The release branches a platform major move asks for (D12); the host creates them.</param>
public sealed record ReleaseRunResult(ReleasePlan Plan, IReadOnlyList<TagWriteResult> Tags, IReadOnlyList<string> Warnings,
	IReadOnlyDictionary<string, ReleaseNotesText>? Notes = null, IReadOnlyList<ReleaseBranchCut>? BranchCuts = null)
{
	public bool Succeeded => Plan.Refused.Count == 0 && Tags.All(t => t.Outcome is TagOutcome.Created or TagOutcome.AlreadyPresent or TagOutcome.Planned);
}

/// <summary>
/// The tags a plan-only run decided (#552 D1), for the release half to write later in the same workflow run. It holds
/// data only, so it crosses jobs as a job output and reaches the server as a request body: both are untrusted, and
/// <see cref="ReleaseRun.ReleaseAsync"/> validates every field before it writes anything.
/// </summary>
/// <param name="Fingerprint">The tag history the plan was decided against, without the plan's own tags (D3).</param>
public sealed record TagPlan(string Commit, string Fingerprint, string? Run, IReadOnlyList<PlannedTag> Tags);

public sealed record PlannedTag(string Name, string? ApiHash);

/// <summary>The tag history changed after the plan was made, so its versions may be wrong (#552 D3).</summary>
public sealed class StalePlanException(string message) : Exception(message);

/// <summary>The tag writer of a plan-only run (#552 D1): it records every tag and writes none.</summary>
public sealed class RecordingTagWriter : ITagWriter
{
	public IReadOnlyList<TagToWrite> Recorded { get; private set; } = [];

	public Task<IReadOnlyList<TagWriteResult>> WriteAsync(IReadOnlyList<TagToWrite> tags, CancellationToken cancellationToken = default)
	{
		Recorded = tags;
		return Task.FromResult<IReadOnlyList<TagWriteResult>>([.. tags.Select(t => new TagWriteResult(t.Tag, TagOutcome.Planned))]);
	}
}

/// <summary>The run cannot plan at all: a malformed manifest, or a packed package the graph does not hold (D5).</summary>
public sealed class ReleaseRunException(string message) : Exception(message);

/// <summary>
/// One versioning run, the same on both hosts (D2, D16): read the graph of the built commit from its tree, read the tag
/// history, plan, and write the tags. The repository is a git directory holding the commit, its history back to the
/// last tags, and those tags; the hosts differ only in how they fetched it and in the <see cref="ITagWriter"/>'s git.
/// </summary>
public static class ReleaseRun
{
	public static async Task<ReleaseRunResult> RunAsync(GitCli git, ITagWriter writer, ReleaseRunRequest request, CancellationToken cancellationToken = default)
	{
		var errors = request.Manifest.Validate();
		if (errors.Count > 0) throw new ReleaseRunException("The release manifest is malformed: " + string.Join(" ", errors));
		var head = request.Manifest.Commit!;

		var graph = await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(git, head, cancellationToken), request.Settings.Ignore, cancellationToken);
		var missing = request.Manifest.Packages!
			.Where(p => p.Packed && !graph.Packages.Any(n => n.Id == p.Id && n.Kind == p.PackageKind))
			.Select(p => p.Id)
			.ToList();
		if (missing.Count > 0)
			throw new ReleaseRunException($"The build packed {string.Join(", ", missing)}, which the repository's projects do not declare as publishable packages"
				+ (graph.Warnings.Count > 0 ? ": " + string.Join(" ", graph.Warnings) : "."));

		var plan = await ReleasePlanner.PlanAsync(new ReleaseContext
		{
			Graph = graph,
			Head = head,
			Tags = await TagHistory.ReadAsync(git, cancellationToken),
			Diff = new GitInputDiff(git),
			Settings = request.Settings,
			Build = request.Manifest.BuildFacts(),
			Sdk = request.Manifest.Sdk,
			OnDefaultBranch = request.OnDefaultBranch,
			BranchMajor = request.BranchMajor,
		}, cancellationToken);

		// All or nothing: a refused package (a major past its line, a lockstep group split across majors) stops the run
		// before any tag is pushed, so a rerun after the fix starts from the same history.
		if (plan.Refused.Count > 0) return new ReleaseRunResult(plan, [], graph.Warnings);

		var tags = plan.Releases
			.Where(r => !r.TaggedByHand)
			.Select(r => new TagToWrite(r.Tag, head, new TagAnnotation(r.ApiHash, null, request.Run)))
			.ToList();
		var written = tags.Count == 0 ? [] : await writer.WriteAsync(tags, cancellationToken);

		// Notes for every tag now in place, the already-present ones too: a rerun after a failed Release creates what is missing.
		var notes = new Dictionary<string, ReleaseNotesText>(StringComparer.Ordinal);
		var placed = written.Where(t => t.Outcome is TagOutcome.Created or TagOutcome.AlreadyPresent or TagOutcome.Planned).Select(t => t.Tag.Name).ToHashSet(StringComparer.Ordinal);
		var placedReleases = plan.Releases.Where(r => placed.Contains(r.Tag.Name)).ToList();
		foreach (var release in placedReleases)
			notes[release.Tag.Name] = await ReleaseNotes.ForAsync(git, head, release, cancellationToken);

		// A cut follows a tag in place, never a refused or failed one: the branch marks a line that was really left.
		var warnings = graph.Warnings.ToList();
		var cuts = request.OnDefaultBranch
			? await ReleaseBranchCuts.ForAsync(git, head, placedReleases, request.Settings.ReleaseBranches, warnings, cancellationToken)
			: [];
		return new ReleaseRunResult(plan, written, warnings, notes, cuts);
	}

	/// <summary>
	/// The plan half (#552 D1): <see cref="RunAsync"/> with a <see cref="RecordingTagWriter"/>, so the versions, notes and
	/// branch cuts are exactly a run's, and no tag is written. <see cref="TagPlan"/> is null when the plan refused anything.
	/// </summary>
	public static async Task<(ReleaseRunResult Result, TagPlan? Tags)> PlanOnlyAsync(GitCli git, ReleaseRunRequest request, CancellationToken cancellationToken = default)
	{
		var recorder = new RecordingTagWriter();
		var result = await RunAsync(git, recorder, request, cancellationToken);
		if (result.Plan.Refused.Count > 0) return (result, null);
		var names = recorder.Recorded.Select(t => t.Tag.Name).ToHashSet(StringComparer.Ordinal);
		var fingerprint = (await TagHistory.ReadAsync(git, cancellationToken)).Fingerprint(names);
		return (result, new TagPlan(request.Manifest.Commit!, fingerprint, request.Run,
			[.. recorder.Recorded.Select(t => new PlannedTag(t.Tag.Name, t.Annotation.ApiHash))]));
	}

	/// <summary>
	/// The release half (#552 D2, D3): writes exactly the planned tags, deciding nothing again. It refuses with
	/// <see cref="StalePlanException"/> when the tag history moved since the plan; the plan's own tags are left out of
	/// that comparison, so a release that pushed some of them before it failed can run again. The caller checks that
	/// the commit is the one its run built (HEAD locally, the OIDC token's <c>sha</c> on the server).
	/// </summary>
	public static async Task<IReadOnlyList<TagWriteResult>> ReleaseAsync(GitCli git, ITagWriter writer, TagPlan plan, CancellationToken cancellationToken = default)
	{
		var commit = GitCli.RequireObjectId(plan.Commit);
		var tags = new List<TagToWrite>();
		foreach (var planned in plan.Tags)
		{
			if (!VersionTag.TryParse(planned.Name, out var tag)) throw new VersioningException($"'{planned.Name}' is not a version tag.");
			var annotation = new TagAnnotation(planned.ApiHash, null, plan.Run);
			annotation.Format(tag); // validates the hash and the run URL before anything is written
			tags.Add(new TagToWrite(tag, commit, annotation));
		}

		await RequireFreshAsync(git, tags.Select(t => t.Tag.Name).ToHashSet(StringComparer.Ordinal), plan.Fingerprint, cancellationToken);
		return tags.Count == 0 ? [] : await writer.WriteAsync(tags, cancellationToken);
	}

	/// <summary>Throws <see cref="StalePlanException"/> unless the tag history, without <paramref name="own"/>, is the one the plan saw (#552 D3).</summary>
	public static async Task RequireFreshAsync(GitCli git, IReadOnlySet<string> own, string fingerprint, CancellationToken cancellationToken = default)
	{
		if ((await TagHistory.ReadAsync(git, cancellationToken)).Fingerprint(own) != fingerprint)
			throw new StalePlanException("The version tags changed after this plan was made (another run or a hand-pushed tag), so its versions may be wrong. "
				+ "Use \"Re-run all jobs\" to plan again; \"Re-run failed jobs\" reuses this plan.");
	}

	/// <summary>
	/// The plan for <paramref name="head"/> without a build (D3 trigger B): the static graph, the tags and the input diff,
	/// no API hashes. It answers only whether a run would release anything; the versions it computes are not the ones the
	/// run will tag, because an unknown API hash always counts as changed.
	/// </summary>
	public static async Task<(ReleasePlan Plan, IReadOnlyList<string> Warnings)> PlanAsync(GitCli git, string head, VersioningSettings settings, bool onDefaultBranch,
		int? branchMajor, CancellationToken cancellationToken = default)
	{
		var graph = await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(git, GitCli.RequireObjectId(head), cancellationToken), settings.Ignore, cancellationToken);
		var plan = await ReleasePlanner.PlanAsync(new ReleaseContext
		{
			Graph = graph,
			Head = head,
			Tags = await TagHistory.ReadAsync(git, cancellationToken),
			Diff = new GitInputDiff(git),
			Settings = settings,
			OnDefaultBranch = onDefaultBranch,
			BranchMajor = branchMajor,
		}, cancellationToken);
		return (plan, graph.Warnings);
	}
}

/// <summary>Release-branch patterns (D10): <c>versioning.releaseBranches</c>, default <c>release/*.x</c>.</summary>
public static class ReleaseBranches
{
	public static IReadOnlyList<string> Default { get; } = ["release/*.x"];

	/// <summary>
	/// Whether <paramref name="branch"/> is a release line. Its major is what a pattern's single <c>*</c> matched when that
	/// is a number (<c>release/8.x</c> → 8), else null: the line is then held to the major its last tag has.
	/// </summary>
	public static bool IsMatch(IReadOnlyList<string> patterns, string branch, out int? major)
	{
		major = null;
		foreach (var pattern in patterns)
		{
			if (!PathGlob.Segment(pattern, branch)) continue;
			var star = pattern.IndexOf('*');
			if (star >= 0 && star == pattern.LastIndexOf('*') && pattern.IndexOf('?') < 0)
			{
				var suffix = pattern.Length - star - 1;
				var captured = branch.Substring(star, branch.Length - star - suffix);
				if (captured.Length is > 0 and <= 9 && captured.All(char.IsAsciiDigit)) major = int.Parse(captured, System.Globalization.CultureInfo.InvariantCulture);
			}
			return true;
		}
		return false;
	}
}
