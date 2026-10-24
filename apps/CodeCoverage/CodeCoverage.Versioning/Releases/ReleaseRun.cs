using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>One run's inputs beside the repository: the manifest, the repository's settings and the branch's line (D10).</summary>
public sealed record ReleaseRunRequest(ReleaseManifest Manifest, VersioningSettings Settings, bool OnDefaultBranch, int? BranchMajor);

/// <summary>What a run planned and what became of each tag. <see cref="Tags"/> is empty when the plan refused anything.</summary>
public sealed record ReleaseRunResult(ReleasePlan Plan, IReadOnlyList<TagWriteResult> Tags, IReadOnlyList<string> Warnings)
{
	public bool Succeeded => Plan.Refused.Count == 0 && Tags.All(t => t.Outcome is TagOutcome.Created or TagOutcome.AlreadyPresent);
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

		var graph = await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(git, head, cancellationToken), cancellationToken);
		var missing = request.Manifest.Packages!
			.Where(p => p.Packed && !graph.Packages.Any(n => n.Id == p.Id && n.Kind == p.PackageKind))
			.Select(p => p.Id)
			.ToList();
		if (missing.Count > 0)
			throw new ReleaseRunException($"The build packed {string.Join(", ", missing)}, which the repository's projects do not declare as publishable packages.");

		var plan = await ReleasePlanner.PlanAsync(new ReleaseContext
		{
			Graph = graph,
			Head = head,
			Tags = await TagHistory.ReadAsync(git, cancellationToken),
			Diff = new GitInputDiff(git),
			Settings = request.Settings,
			Build = request.Manifest.BuildFacts(),
			OnDefaultBranch = request.OnDefaultBranch,
			BranchMajor = request.BranchMajor,
		}, cancellationToken);

		// All or nothing: a refused package (a major past its line, a lockstep group split across majors) stops the run
		// before any tag is pushed, so a rerun after the fix starts from the same history.
		if (plan.Refused.Count > 0) return new ReleaseRunResult(plan, [], graph.Warnings);

		var tags = plan.Releases
			.Where(r => !r.TaggedByHand)
			.Select(r => new TagToWrite(r.Tag, head, new TagAnnotation(r.ApiHash, null)))
			.ToList();
		var written = tags.Count == 0 ? [] : await writer.WriteAsync(tags, cancellationToken);
		return new ReleaseRunResult(plan, written, graph.Warnings);
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
