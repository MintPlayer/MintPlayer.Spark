using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>An old line branch and the name the line pattern gives it.</summary>
public sealed record LineRename(string From, string To, ReleaseLine Line);

/// <summary>A branch that looks like a line but is not renamed, and why (shown in the onboarding pull request).</summary>
public sealed record LineRenameSkip(string Branch, string Reason);

public sealed record LineRenamePlan(IReadOnlyList<LineRename> Renames, IReadOnlyList<LineRenameSkip> Skipped);

/// <summary>
/// #568 D2 (#571 M10b): which existing branches are release lines under another name, and what the line pattern calls
/// them. The onboarding pull request lists the table, and the bot renames once it merges.
/// </summary>
/// <remarks>
/// A line under another name is <c>release/</c> followed by a major, with or without an ecosystem
/// (<c>release/dotnet-6</c>, <c>release/6.0</c>, <c>release/3.1</c>), or an ecosystem and a major without the prefix
/// (<c>angular-17</c>): the schemes in use (coverage_backport_PRD §5.1.1). Anything with more after the major
/// (<c>angular-21b</c>, <c>angular-21-def</c>) is not a line. Without an ecosystem in the name, the repository's only
/// ecosystem is meant; a repository with several is not guessed for. A rename never overwrites a branch, and two old
/// branches that would get one name are both left for a person.
/// </remarks>
public static partial class LineRenames
{
	public static LineRenamePlan Plan(IReadOnlyList<string> branches, string defaultBranch, IReadOnlySet<string> ecosystems, ReleaseLinePattern pattern)
	{
		ArgumentNullException.ThrowIfNull(branches);
		ArgumentNullException.ThrowIfNull(pattern);
		var existing = branches.ToHashSet(StringComparer.Ordinal);
		var skipped = new List<LineRenameSkip>();
		var candidates = new List<LineRename>();
		foreach (var branch in branches.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
		{
			if (branch == defaultBranch || pattern.TryMatch(branch, out _) || OldLine().Match(branch) is not { Success: true } match)
				continue;
			var ecosystem = match.Groups["ecosystem"].Success ? match.Groups["ecosystem"].Value : null;
			if (ecosystem is null)
			{
				if (ecosystems.Count != 1)
				{
					skipped.Add(new(branch, ecosystems.Count == 0
						? "its name has no ecosystem, and the repository releases no packages"
						: $"its name has no ecosystem, and the repository releases {string.Join(" and ", ecosystems.Order(StringComparer.Ordinal))} packages: which ecosystem it serves is not clear, so rename it by hand"));
					continue;
				}
				ecosystem = ecosystems.Single();
			}
			else if (!ecosystems.Contains(ecosystem))
			{
				skipped.Add(new(branch, $"the repository releases no {ecosystem} packages"));
				continue;
			}
			var line = new ReleaseLine(ecosystem, int.Parse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture));
			candidates.Add(new LineRename(branch, pattern.Format(line), line));
		}

		var renames = new List<LineRename>();
		foreach (var group in candidates.GroupBy(c => c.To, StringComparer.Ordinal))
		{
			if (existing.Contains(group.Key))
				skipped.AddRange(group.Select(c => new LineRenameSkip(c.From, $"{group.Key} exists already")));
			else if (group.Count() > 1)
				skipped.AddRange(group.Select(c => new LineRenameSkip(c.From, $"{string.Join(" and ", group.Select(g => g.From))} would both become {group.Key}")));
			else
				renames.Add(group.Single());
		}
		return new LineRenamePlan(
			[.. renames.OrderBy(r => r.From, StringComparer.Ordinal)],
			[.. skipped.OrderBy(s => s.Branch, StringComparer.Ordinal)]);
	}

	[GeneratedRegex(@"^(?:release/(?:(?<ecosystem>[a-z][a-z0-9]{0,31})-)?|(?<ecosystem>[a-z][a-z0-9]{0,31})-)v?(?<major>[1-9][0-9]{0,8})(?:\.(?:[0-9]{1,9}|x))?$", RegexOptions.CultureInvariant)]
	private static partial Regex OldLine();
}
