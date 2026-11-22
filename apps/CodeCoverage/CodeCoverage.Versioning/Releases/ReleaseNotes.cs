using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>One Release's title, notes and flag (D15), the same on both hosts.</summary>
public sealed record ReleaseNotesText(string Name, string Body, bool Prerelease);

/// <summary>
/// The notes of one Release (D15): the subjects of the commits that touched the package since its previous tag, grouped
/// <c>feat</c> / <c>fix</c> / other. With title correction (D14) and squash merges a subject is the PR's corrected title
/// with its <c>(#PR)</c>. A merge commit's subject is GitHub's "Merge pull request #N from …", so its body's first line,
/// the PR title, stands in for it.
/// </summary>
public static partial class ReleaseNotes
{
	/// <summary>The most commits one Release lists; GitHub caps a body at 125,000 characters.</summary>
	public const int MaxCommits = 200;

	public static async Task<ReleaseNotesText> ForAsync(GitCli git, string head, PlannedRelease release, CancellationToken cancellationToken = default)
	{
		var subjects = release.Since is { } since && release.Inputs is { Count: > 0 } inputs
			? await SubjectsAsync(git, since, head, inputs, cancellationToken)
			: [];
		return Compose(release, subjects);
	}

	/// <summary>
	/// The first-parent subjects in <paramref name="from"/>..<paramref name="to"/> below <paramref name="paths"/>, newest
	/// first: on the default branch every first-parent commit is one merged pull request. A merge that is not a pull
	/// request's (a release line's "Promote …", #693 D4) stands for the commits it brings in, so it is replaced by the
	/// first-parent subjects of its first parent..second parent.
	/// </summary>
	public static async Task<IReadOnlyList<string>> SubjectsAsync(GitCli git, string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		var subjects = new List<string>();
		await CollectAsync(git, GitCli.RequireObjectId(from), GitCli.RequireObjectId(to), paths, subjects, MaxExpansionDepth, cancellationToken);
		return subjects;
	}

	/// <summary>How deep promotions nest: a promote of a line that itself merged a promote. Deeper merges stay one line.</summary>
	private const int MaxExpansionDepth = 3;

	private static async Task CollectAsync(GitCli git, string from, string to, IReadOnlyList<string> paths, List<string> subjects, int depth, CancellationToken cancellationToken)
	{
		var remaining = MaxCommits - subjects.Count;
		if (remaining <= 0) return;
		string[] arguments = ["log", "--first-parent", "-n", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture), "--format=%P%x1f%s%x1f%b%x1e",
			$"{from}..{to}", "--", .. paths.Select(p => ":(literal)" + p)];
		var result = (await git.RunAsync(arguments, cancellationToken: cancellationToken)).EnsureSuccess("log");
		var entries = Encoding.UTF8.GetString(result.Output).Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (var entry in entries)
		{
			if (subjects.Count >= MaxCommits) return;
			var parts = entry.Split('\x1f', 3);
			if (parts.Length < 2) continue;
			var parents = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var body = parts.Length > 2 ? parts[2] : "";
			if (parents.Length > 1 && depth > 0 && !MergePullRequest().IsMatch(parts[1]))
			{
				await CollectAsync(git, GitCli.RequireObjectId(parents[0]), GitCli.RequireObjectId(parents[1]), paths, subjects, depth - 1, cancellationToken);
				continue;
			}
			var subject = Subject(parts[1], body);
			if (subject.Length > 0) subjects.Add(subject);
		}
	}

	private static string Subject(string subject, string body)
	{
		if (MergePullRequest().Match(subject) is not { Success: true } merge) return subject.Trim();
		var title = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
		return title is null ? subject.Trim() : $"{title} (#{merge.Groups[1].Value})";
	}

	/// <summary>The notes from the subjects alone: pure, so both hosts and the tests agree.</summary>
	public static ReleaseNotesText Compose(PlannedRelease release, IReadOnlyList<string> subjects)
	{
		var features = new List<string>();
		var fixes = new List<string>();
		var other = new List<string>();
		foreach (var subject in subjects.Distinct(StringComparer.Ordinal))
		{
			var type = ConventionalType().Match(subject) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
			(type switch { "feat" => features, "fix" => fixes, _ => other }).Add(subject);
		}

		var body = new StringBuilder();
		Section(body, "Features", features);
		Section(body, "Fixes", fixes);
		Section(body, "Other changes", other);
		var follows = (release.Follows ?? []).Select(t => $"Dependency update: {t.PackageId} {t.Version}").ToList();
		if (subjects.Count == 0 && follows.Count > 0)
			foreach (var line in follows) body.Append(line).Append('\n');
		else
			Section(body, "Dependencies", follows);
		if (body.Length == 0)
			body.Append(release.Since is null ? "First release." : string.Join("\n", release.Reasons.Select(r => $"- {r}"))).Append('\n');

		return new ReleaseNotesText($"{release.Id} {release.Version}", body.ToString().TrimEnd('\n'), release.Version.IsPrerelease);
	}

	private static void Section(StringBuilder body, string heading, List<string> lines)
	{
		if (lines.Count == 0) return;
		if (body.Length > 0) body.Append('\n');
		body.Append("### ").Append(heading).Append('\n');
		foreach (var line in lines) body.Append("- ").Append(line).Append('\n');
	}

	[GeneratedRegex(@"^([A-Za-z]+)(\([^)]*\))?!?:\s")]
	private static partial Regex ConventionalType();

	[GeneratedRegex(@"^Merge pull request #(\d+) from ")]
	private static partial Regex MergePullRequest();
}
