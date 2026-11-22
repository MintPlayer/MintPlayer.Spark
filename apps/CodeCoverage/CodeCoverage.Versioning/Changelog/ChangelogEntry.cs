using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Changelog;

/// <summary>
/// One commit of a release's range (#693): its subject (a merge commit's PR title stands in for "Merge pull request …"),
/// its body, and, for a <c>git cherry-pick -x</c>, the pull request of the commit it was picked from.
/// </summary>
public sealed record ChangeCommit(string Subject, string Body, int? CherryPickOf = null);

/// <summary>
/// What a host knows about one pull request at release time (#693 D6, D9): the current title and body, the labels, whether
/// a linked issue is labelled <c>bug</c>, and the original a backport PR names in its body.
/// </summary>
public sealed record PullRequestFacts(int Number, string Title, string? Body, IReadOnlyList<string> Labels, bool LinkedIssueIsBug = false, int? BackportOf = null);

/// <summary>
/// The pull-request port of the changelog (#693): each host answers it from the forge's API. The engine itself reads only
/// git, so without it the notes come from the commit subjects alone.
/// </summary>
public interface IPullRequestFacts
{
	/// <summary>The facts of the pull requests it can read; a number it cannot read is left out.</summary>
	Task<IReadOnlyDictionary<int, PullRequestFacts>> GetAsync(IReadOnlyCollection<int> numbers, CancellationToken cancellationToken = default);
}

/// <summary>The sections of a release's notes, in the order they are written (#693 D6).</summary>
public enum ChangeSection
{
	Breaking,
	Features,
	Fixes,
	Performance,
	Reverts,
	Changes,
	Dependencies,
	/// <summary><c>chore</c>, <c>ci</c>, <c>test</c>, <c>docs</c>, <c>build</c>, <c>style</c>, <c>refactor</c>: not written.</summary>
	Dropped,
}

/// <summary>
/// One changelog line (#693 D6): the text without type and suffixes, the type when an explicit signal gave one, the pull
/// request and the issues it closes. <see cref="Original"/> is the pull request a backport was made from (D5): it is the
/// key, so a change is one entry however it reached the line.
/// </summary>
public sealed record ChangelogEntry(string Text, string? Type, bool Breaking, int? PullRequest, IReadOnlyList<int> Issues, int? Original = null, bool Dependency = false)
{
	public int? Key => Original ?? PullRequest;

	public ChangeSection Section => Dependency ? ChangeSection.Dependencies
		: Breaking ? ChangeSection.Breaking
		: Type switch
		{
			null => ChangeSection.Changes,
			"feat" => ChangeSection.Features,
			"fix" => ChangeSection.Fixes,
			"perf" => ChangeSection.Performance,
			"revert" => ChangeSection.Reverts,
			_ => ChangeSection.Dropped,
		};
}

/// <summary>The changelog's pure rules (#693): parse the commits, apply the pull requests' facts, deduplicate.</summary>
public static partial class ChangelogEntries
{
	/// <summary>The label that keeps a pull request out of the changelog (D9).</summary>
	public const string SkipLabel = "changelog: skip";

	/// <summary>
	/// An entry from a commit subject such as <c>fix(core)!: null id (#251) (#610)</c>: the last single-number suffix is the
	/// pull request the squash appended, the suffixes before it are the issues title correction added. A backport's
	/// <c>[dotnet-11.x] </c> prefix goes.
	/// </summary>
	public static ChangelogEntry Parse(ChangeCommit commit)
	{
		var subject = BackportPrefix().Replace(commit.Subject.Trim(), "");
		var groups = new List<IReadOnlyList<int>>();
		while (Suffix().Match(subject) is { Success: true } m)
		{
			groups.Insert(0, [.. Number().Matches(m.Value).Select(n => int.Parse(n.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))]);
			subject = subject[..m.Index].TrimEnd();
		}

		int? pullRequest = null;
		if (groups.Count > 0 && groups[^1].Count == 1)
		{
			pullRequest = groups[^1][0];
			groups.RemoveAt(groups.Count - 1);
		}

		var (type, scope, bang, text) = PrTitleBuilder.Parse(subject);
		var original = BackportTrailer().Match(commit.Body) is { Success: true } trailer
			? int.Parse(trailer.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
			: commit.CherryPickOf;
		return new ChangelogEntry(text, type, bang == "!" || BreakingFooter().IsMatch(commit.Body), pullRequest,
			[.. groups.SelectMany(g => g).Distinct()], original == pullRequest ? null : original, IsDependencyScope(scope));
	}

	/// <summary>The pull request a subject ends in, or null: the key of a cherry-picked commit's origin.</summary>
	public static int? PullRequestOf(string subject) => Parse(new ChangeCommit(subject, "")).PullRequest;

	/// <summary>
	/// Applies what the forge says about each pull request (D6, D9): <see cref="SkipLabel"/> drops it, a <c>## Changelog</c>
	/// body section replaces its entry with its bullets, an untyped entry takes the type of the PR's ticked template box, its
	/// current title or a <c>bug</c>-labelled linked issue, and a backport PR's body names its original.
	/// </summary>
	public static IReadOnlyList<ChangelogEntry> Resolve(IEnumerable<ChangelogEntry> entries, IReadOnlyDictionary<int, PullRequestFacts> facts)
	{
		var resolved = new List<ChangelogEntry>();
		foreach (var entry in entries)
		{
			if (entry.PullRequest is not { } number || !facts.TryGetValue(number, out var pull))
			{
				resolved.Add(entry);
				continue;
			}
			if (pull.Labels.Any(l => string.Equals(l.Trim(), SkipLabel, StringComparison.OrdinalIgnoreCase))) continue;

			var typed = entry with { Original = entry.Original ?? (pull.BackportOf == number ? null : pull.BackportOf) };
			if (typed.Type is null && !typed.Dependency)
			{
				var title = PrTitleBuilder.Parse(pull.Title);
				var type = PrTitleBuilder.TemplateType(pull.Body) ?? title.Type ?? (pull.LinkedIssueIsBug ? "fix" : null);
				typed = typed with { Type = type, Breaking = typed.Breaking || (type is not null && type == title.Type && title.Bang == "!") };
			}

			if (ChangelogSection(pull.Body) is not { } bullets)
			{
				resolved.Add(typed);
				continue;
			}
			foreach (var bullet in bullets)
			{
				var own = PrTitleBuilder.Parse(bullet);
				if (own.Text.Length == 0) continue;
				resolved.Add(typed with
				{
					Text = own.Text,
					Type = own.Type ?? typed.Type,
					Breaking = own.Type is not null ? own.Bang == "!" : typed.Breaking,
					Dependency = own.Type is not null ? IsDependencyScope(own.Scope) : typed.Dependency,
				});
			}
		}
		return resolved;
	}

	/// <summary>One entry per key, the first (newest) kept (D5); entries without a pull request are kept once per text.</summary>
	public static IReadOnlyList<ChangelogEntry> Deduplicate(IEnumerable<ChangelogEntry> entries)
	{
		var keys = new HashSet<int>();
		var texts = new HashSet<string>(StringComparer.Ordinal);
		return [.. entries.Where(e => e.Key is { } key ? keys.Add(key) : texts.Add(e.Text))];
	}

	/// <summary>
	/// The bullets of the body's <c>## Changelog</c> section (D9), or null when there is no such section. An empty section,
	/// or one that says only <c>none</c>, gives no bullets: the pull request has no entry.
	/// </summary>
	internal static IReadOnlyList<string>? ChangelogSection(string? body)
	{
		if (string.IsNullOrEmpty(body)) return null;
		var text = HtmlComment().Replace(body.Replace("\r", ""), "");
		if (SectionHeading().Match(text) is not { Success: true } heading) return null;
		var rest = text[(heading.Index + heading.Length)..];
		if (NextHeading().Match(rest) is { Success: true } next) rest = rest[..next.Index];
		if (string.Equals(rest.Trim(), "none", StringComparison.OrdinalIgnoreCase)) return [];
		return [.. Bullet().Matches(rest).Select(m => m.Groups[1].Value.Trim()).Where(b => b.Length > 0 && !string.Equals(b, "none", StringComparison.OrdinalIgnoreCase))];
	}

	/// <summary>Renovate's and Dependabot's scopes: <c>chore(deps)</c>, <c>build(deps-dev)</c>, <c>fix(deps)</c>.</summary>
	private static bool IsDependencyScope(string scope) => scope is "(deps)" or "(deps-dev)";

	[GeneratedRegex(@"^\[[^\]\s]+\]\s+")]
	private static partial Regex BackportPrefix();

	[GeneratedRegex(@"\s*\(#\d+(?:\s*/\s*#\d+)*\)$")]
	private static partial Regex Suffix();

	[GeneratedRegex(@"#(\d{1,9})")]
	private static partial Regex Number();

	[GeneratedRegex(@"(?im)^Backport-of:\s*(?:[A-Za-z0-9-]+/[A-Za-z0-9._-]+)?#(\d{1,9})\s*$")]
	private static partial Regex BackportTrailer();

	[GeneratedRegex(@"(?m)^BREAKING[ -]CHANGE:\s")]
	private static partial Regex BreakingFooter();

	[GeneratedRegex(@"(?s)<!--.*?-->")]
	private static partial Regex HtmlComment();

	[GeneratedRegex(@"(?im)^##[ \t]+Changelog[ \t]*$")]
	private static partial Regex SectionHeading();

	[GeneratedRegex(@"(?m)^#{1,2}[ \t]")]
	private static partial Regex NextHeading();

	[GeneratedRegex(@"(?m)^[ \t]*[-*][ \t]+(.+)$")]
	private static partial Regex Bullet();
}
