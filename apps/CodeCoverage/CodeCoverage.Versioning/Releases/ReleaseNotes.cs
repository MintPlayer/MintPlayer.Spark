using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Versioning.Changelog;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>One Release's title, notes and flag (D15), the same on both hosts.</summary>
/// <param name="DependencyOnly">
/// #693 D7: the package moved only because a dependency did. It gets no Release of its own; the run's Release lists it.
/// </param>
public sealed record ReleaseNotesText(string Name, string Body, bool Prerelease, bool DependencyOnly = false);

/// <summary>
/// The notes of one Release (D15, #693 D6): one entry per pull request that touched the package's inputs since its previous
/// tag, in sections by type. With title correction (D14) and squash merges a subject is the PR's corrected title with its
/// <c>(#PR)</c>. A merge commit's subject is GitHub's "Merge pull request #N from …", so its body's first line, the PR
/// title, stands in for it. With <see cref="IPullRequestFacts"/> the pull requests' labels and bodies apply too (D9).
/// </summary>
public static partial class ReleaseNotes
{
	/// <summary>The most commits one Release lists; GitHub caps a body at 125,000 characters.</summary>
	public const int MaxCommits = 200;

	public static async Task<ReleaseNotesText> ForAsync(GitCli git, string head, PlannedRelease release, IPullRequestFacts? pullRequests = null,
		CancellationToken cancellationToken = default)
	{
		var commits = release.Since is { } since && release.Inputs is { Count: > 0 } inputs
			? await CommitsAsync(git, since, head, inputs, cancellationToken)
			: [];
		return Compose(release, await EntriesAsync(commits, pullRequests, cancellationToken));
	}

	/// <summary>The entries of <paramref name="commits"/>: parsed, resolved against the pull requests' facts, deduplicated.</summary>
	public static async Task<IReadOnlyList<ChangelogEntry>> EntriesAsync(IReadOnlyList<ChangeCommit> commits, IPullRequestFacts? pullRequests,
		CancellationToken cancellationToken = default)
	{
		IReadOnlyList<ChangelogEntry> entries = [.. commits.Select(ChangelogEntries.Parse)];
		var numbers = entries.Where(e => e.PullRequest is not null).Select(e => e.PullRequest!.Value).Distinct().ToList();
		if (pullRequests is not null && numbers.Count > 0)
			entries = ChangelogEntries.Resolve(entries, await pullRequests.GetAsync(numbers, cancellationToken));
		return ChangelogEntries.Deduplicate(entries);
	}

	/// <summary>The subjects of <see cref="CommitsAsync"/>, for callers that need the text only.</summary>
	public static async Task<IReadOnlyList<string>> SubjectsAsync(GitCli git, string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
		=> [.. (await CommitsAsync(git, from, to, paths, cancellationToken)).Select(c => c.Subject)];

	/// <summary>
	/// The first-parent commits in <paramref name="from"/>..<paramref name="to"/> below <paramref name="paths"/>, newest
	/// first: on the default branch every first-parent commit is one merged pull request. A merge that is not a pull
	/// request's (a release line's "Promote …", #693 D4) stands for the commits it brings in, so it is replaced by the
	/// first-parent commits of its first parent..second parent.
	/// </summary>
	public static async Task<IReadOnlyList<ChangeCommit>> CommitsAsync(GitCli git, string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		var commits = new List<ChangeCommit>();
		await CollectAsync(git, GitCli.RequireObjectId(from), GitCli.RequireObjectId(to), paths, commits, MaxExpansionDepth, cancellationToken);
		return commits;
	}

	/// <summary>How deep promotions nest: a promote of a line that itself merged a promote. Deeper merges stay one line.</summary>
	private const int MaxExpansionDepth = 3;

	private static async Task CollectAsync(GitCli git, string from, string to, IReadOnlyList<string> paths, List<ChangeCommit> commits, int depth, CancellationToken cancellationToken)
	{
		var remaining = MaxCommits - commits.Count;
		if (remaining <= 0) return;
		string[] arguments = ["log", "--first-parent", "-n", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture), "--format=%P%x1f%s%x1f%b%x1e",
			$"{from}..{to}", "--", .. paths.Select(p => ":(literal)" + p)];
		var result = (await git.RunAsync(arguments, cancellationToken: cancellationToken)).EnsureSuccess("log");
		var entries = Encoding.UTF8.GetString(result.Output).Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (var entry in entries)
		{
			if (commits.Count >= MaxCommits) return;
			var parts = entry.Split('\x1f', 3);
			if (parts.Length < 2) continue;
			var parents = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var body = parts.Length > 2 ? parts[2] : "";
			if (parents.Length > 1 && depth > 0 && !MergePullRequest().IsMatch(parts[1]))
			{
				await CollectAsync(git, GitCli.RequireObjectId(parents[0]), GitCli.RequireObjectId(parents[1]), paths, commits, depth - 1, cancellationToken);
				continue;
			}
			var subject = Subject(parts[1], body);
			if (subject.Length == 0) continue;
			commits.Add(new ChangeCommit(subject, body, await CherryPickOriginAsync(git, body, cancellationToken)));
		}
	}

	/// <summary>
	/// #693 D5: the pull request of the commit a <c>git cherry-pick -x</c> names, read from that commit's subject. Null when
	/// there is no such line, or the commit is not in the clone.
	/// </summary>
	private static async Task<int?> CherryPickOriginAsync(GitCli git, string body, CancellationToken cancellationToken)
	{
		if (CherryPicked().Match(body) is not { Success: true } picked) return null;
		var shown = await git.RunAsync(["log", "-1", "--format=%s", picked.Groups[1].Value], cancellationToken: cancellationToken);
		return shown.ExitCode == 0 ? ChangelogEntries.PullRequestOf(Encoding.UTF8.GetString(shown.Output).Trim()) : null;
	}

	private static string Subject(string subject, string body)
	{
		if (MergePullRequest().Match(subject) is not { Success: true } merge) return subject.Trim();
		var title = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
		return title is null ? subject.Trim() : $"{title} (#{merge.Groups[1].Value})";
	}

	private static readonly (ChangeSection Section, string Heading)[] Headings =
	[
		(ChangeSection.Breaking, "Breaking changes"), (ChangeSection.Features, "Features"), (ChangeSection.Fixes, "Fixes"),
		(ChangeSection.Performance, "Performance"), (ChangeSection.Reverts, "Reverts"), (ChangeSection.Changes, "Changes"),
	];

	/// <summary>The notes from the entries alone: pure, so both hosts and the tests agree.</summary>
	public static ReleaseNotesText Compose(PlannedRelease release, IReadOnlyList<ChangelogEntry> entries)
	{
		var body = new StringBuilder();
		var own = 0;
		var api = release.Api;
		foreach (var (section, heading) in Headings)
		{
			var lines = entries.Where(e => e.Section == section).Select(Line).ToList();
			// #693 M9: a removal from the public API breaks its consumers, whatever the pull request's type said.
			if (section == ChangeSection.Breaking && api is not null) lines.AddRange(api.Removed.Select(l => $"Removed from the public API: {Code(l)}"));
			own += lines.Count;
			Section(body, heading, lines);
		}
		if (api is not null)
		{
			List<string> added = [.. api.Added.Select(l => $"Added {Code(l)}")];
			if (api.Omitted > 0) added.Add($"… and {api.Omitted} more API change{(api.Omitted == 1 ? "" : "s")}");
			own += added.Count;
			Section(body, "Public API", added);
		}

		List<string> dependencies =
		[
			.. entries.Where(e => e.Section == ChangeSection.Dependencies).Select(Line),
			.. (release.Follows ?? []).Select(t => $"Dependency update: {t.PackageId} {t.Version}"),
		];
		if (own == 0 && dependencies.Count > 0)
			foreach (var line in dependencies) body.Append(line).Append('\n');
		else
			Section(body, "Dependencies", dependencies);
		if (body.Length == 0)
			body.Append(release.Since is null ? "First release." : string.Join("\n", release.Reasons.Select(r => $"- {r}"))).Append('\n');

		// Dependency-only: something moved, and none of it is the package's own change (D7). A first release is its own.
		var dependencyOnly = own == 0 && release.Since is not null && (dependencies.Count > 0 || (release.Follows?.Count ?? 0) > 0);
		return new ReleaseNotesText($"{release.Id} {release.Version}", body.ToString().TrimEnd('\n'), release.Version.IsPrerelease, dependencyOnly);
	}

	/// <summary>One line: the text, then the pull request (the original of a backport) and the issues it closes.</summary>
	internal static string Line(ChangelogEntry entry)
	{
		var references = new List<string>();
		if (entry.Key is { } key) references.Add($"#{key}");
		if (entry.Issues.Count > 0) references.Add("closes " + string.Join(", ", entry.Issues.Select(i => $"#{i}")));
		return references.Count == 0 ? entry.Text : $"{entry.Text} ({string.Join(", ", references)})";
	}

	/// <summary>An API line as inline code; a TypeScript template literal type holds backticks, so it gets a double fence.</summary>
	private static string Code(string line) => line.Contains('`') ? $"`` {line} ``" : $"`{line}`";

	private static void Section(StringBuilder body, string heading, List<string> lines)
	{
		if (lines.Count == 0) return;
		if (body.Length > 0) body.Append('\n');
		body.Append("### ").Append(heading).Append('\n');
		foreach (var line in lines) body.Append("- ").Append(line).Append('\n');
	}

	[GeneratedRegex(@"^Merge pull request #(\d+) from ")]
	private static partial Regex MergePullRequest();

	[GeneratedRegex(@"\(cherry picked from commit ([0-9a-f]{40})\)")]
	private static partial Regex CherryPicked();
}
