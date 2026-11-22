namespace CodeCoverage.Versioning.Releases;

/// <summary>One package a run released, as the run's own Release lists it (#693 D7).</summary>
public sealed record RunReleasePackage(string Id, string Version, string Tag, bool DependencyOnly);

/// <summary>
/// The run's own Release (#693 D7), the same text on both hosts (the action's <c>runBody</c> in <c>releases.ts</c>): created
/// last and marked latest, so the repository's "Latest release" is the overview. Its tag holds no <c>/v</c>, so
/// <see cref="Versions.VersionTag.TryParse"/> and the MSBuild prefix match never read it as a version (S2).
/// </summary>
public static class RunRelease
{
	/// <summary>The most <c>.n</c> suffixes tried for one day.</summary>
	public const int MaxPerDay = 20;

	/// <summary><c>release/&lt;line&gt;/&lt;yyyy-MM-dd&gt;</c> on a release line (the branch without <c>release/</c>), else <c>release/&lt;yyyy-MM-dd&gt;</c>.</summary>
	public static string TagBase(string branch, string? defaultBranch, DateOnly date)
	{
		var day = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
		return Line(branch, defaultBranch) is { } line ? $"release/{line}/{day}" : $"release/{day}";
	}

	/// <summary>The <paramref name="n"/>th candidate: the base, then <c>base.2</c>, <c>base.3</c>, ….</summary>
	public static string Tag(string tagBase, int n) => n <= 1 ? tagBase : $"{tagBase}.{n}";

	public static string Name(string branch, string? defaultBranch, DateOnly date)
	{
		var day = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
		return Line(branch, defaultBranch) is { } line ? $"{line} {day}" : day;
	}

	/// <summary>A line per package, linked to its own Release unless it moved only with a dependency.</summary>
	public static string Body(IReadOnlyList<RunReleasePackage> packages, string repositoryUrl)
	{
		var lines = new List<string>();
		var own = packages.Where(p => !p.DependencyOnly).ToList();
		var dependents = packages.Where(p => p.DependencyOnly).ToList();
		if (own.Count > 0)
		{
			lines.Add("### Packages");
			lines.AddRange(own.Select(p => repositoryUrl.Length == 0 ? $"- {p.Id} {p.Version}"
				: $"- [{p.Id} {p.Version}]({repositoryUrl.TrimEnd('/')}/releases/tag/{Uri.EscapeDataString(p.Tag).Replace("%2F", "/")})"));
		}
		if (dependents.Count > 0)
		{
			if (lines.Count > 0) lines.Add("");
			lines.Add("### Dependency updates only");
			lines.AddRange(dependents.Select(p => $"- {p.Id} {p.Version}"));
		}
		return string.Join('\n', lines);
	}

	private static string? Line(string branch, string? defaultBranch)
		=> branch.Length == 0 || branch == defaultBranch ? null
			: branch.StartsWith("release/", StringComparison.Ordinal) && branch.Length > "release/".Length ? branch["release/".Length..] : branch;
}
