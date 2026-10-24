using System.Globalization;
using System.Text.RegularExpressions;
using CodeCoverage.Versioning.Git;

namespace CodeCoverage.Versioning.Releases;

/// <summary>
/// A release branch the run asks its host to create (D12): <see cref="Branch"/> at <see cref="Commit"/>, the release
/// commit's first parent, the last commit of the old platform line. <see cref="PullRequest"/> is the merged pull request
/// to comment on, when the release commit names one.
/// </summary>
public sealed record ReleaseBranchCut(string Branch, int Major, string Commit, IReadOnlyList<string> Packages, int? PullRequest);

/// <summary>
/// Which release branches a default-branch run cuts (D12): one per platform major that a released package left. The
/// first package to move cuts the branch for the whole line, so a later package moving off the same major finds it in
/// place. The engine only decides; each host creates the branch and comments, behind its own switch.
/// </summary>
public static partial class ReleaseBranchCuts
{
	public static async Task<IReadOnlyList<ReleaseBranchCut>> ForAsync(GitCli git, string head, IEnumerable<PlannedRelease> placed,
		IReadOnlyList<string> patterns, ICollection<string> warnings, CancellationToken cancellationToken = default)
	{
		var moved = placed.Where(r => r.MovedFromMajor is not null).GroupBy(r => r.MovedFromMajor!.Value).OrderBy(g => g.Key).ToList();
		if (moved.Count == 0) return [];

		var parent = await git.RunAsync(["rev-parse", "--verify", "--quiet", GitCli.RequireObjectId(head) + "^1^{commit}"], cancellationToken: cancellationToken);
		if (parent.ExitCode != 0)
		{
			warnings.Add($"{head} has no first parent, so no release branch is cut for {string.Join(", ", moved.Select(g => g.Key + ".x"))}.");
			return [];
		}
		var subject = (await git.RunAsync(["log", "-1", "--format=%s", head], cancellationToken: cancellationToken)).EnsureSuccess("log").Text;
		var pullRequest = PullRequestOf(subject);

		var cuts = new List<ReleaseBranchCut>();
		foreach (var line in moved)
		{
			if (NameFor(patterns, line.Key) is not { } branch)
			{
				warnings.Add($"No versioning.releaseBranches pattern has a single '*' to name the {line.Key}.x branch, so none is cut.");
				continue;
			}
			cuts.Add(new ReleaseBranchCut(branch, line.Key, parent.Text.Trim(), line.Select(r => r.Id).ToList(), pullRequest));
		}
		return cuts;
	}

	/// <summary>The branch for <paramref name="major"/>: the first pattern with a single <c>*</c>, the major in its place.</summary>
	public static string? NameFor(IReadOnlyList<string> patterns, int major)
		=> patterns.FirstOrDefault(p => p.Count(c => c == '*') == 1 && !p.Contains('?')) is { } pattern
			? pattern.Replace("*", major.ToString(CultureInfo.InvariantCulture))
			: null;

	/// <summary>The pull request a release commit merged: GitHub's merge subject, or the <c>(#N)</c> a squash merge ends with.</summary>
	public static int? PullRequestOf(string subject)
		=> PullRequestNumber().Match(subject.Trim()) is { Success: true } match
			&& int.TryParse(match.Groups["merge"].Success ? match.Groups["merge"].Value : match.Groups["squash"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
			? number
			: null;

	[GeneratedRegex(@"^Merge pull request #(?<merge>\d{1,9}) from |\(#(?<squash>\d{1,9})\)\z")]
	private static partial Regex PullRequestNumber();
}
