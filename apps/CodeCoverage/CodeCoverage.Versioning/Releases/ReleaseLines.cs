using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>One release line (#568 D1): the packages of one ecosystem at one platform major, e.g. <c>dotnet</c> 10.</summary>
public sealed record ReleaseLine(string Ecosystem, int Major)
{
	public override string ToString() => $"{Ecosystem}-{Major}.x";

	/// <summary>The line <see cref="ToString"/> writes (<c>dotnet-11.x</c>), or null: the key of <c>versioning.lines.prerelease</c>.</summary>
	public static ReleaseLine? TryParse(string? text)
	{
		if (text is null || !text.EndsWith(".x", StringComparison.Ordinal)) return null;
		var dash = text.LastIndexOf('-');
		if (dash <= 0) return null;
		var ecosystem = text[..dash];
		var major = text[(dash + 1)..^2];
		return Ecosystems.IsName(ecosystem) && major is { Length: > 0 and <= 9 } && major[0] != '0' && major.All(char.IsAsciiDigit)
			? new ReleaseLine(ecosystem, int.Parse(major, NumberStyles.None, CultureInfo.InvariantCulture))
			: null;
	}
}

/// <summary>
/// The branch-name pattern of release lines (#568 D2), <c>versioning.lines.pattern</c>, default
/// <c>release/{ecosystem}-{major}.x</c>. Both placeholders are required, exactly once each (D1: no plain
/// <c>release/N.x</c>). An ecosystem is lower-case letters and digits, starting with a letter, so it never swallows the
/// separator in front of the major.
/// </summary>
public sealed class ReleaseLinePattern
{
	public const string EcosystemPlaceholder = "{ecosystem}";
	public const string MajorPlaceholder = "{major}";
	public const string DefaultText = "release/{ecosystem}-{major}.x";

	public static ReleaseLinePattern Default { get; } = Parse(DefaultText);

	private readonly Regex regex;

	private ReleaseLinePattern(string text, Regex regex)
	{
		Text = text;
		this.regex = regex;
	}

	public string Text { get; }

	/// <summary>The pattern, or null with <paramref name="error"/> in the file's terms.</summary>
	public static ReleaseLinePattern? TryParse(string? text, out string? error)
	{
		error = null;
		if (text is null || !VersioningConfig.IsBranchPattern(text.Replace(EcosystemPlaceholder, "e", StringComparison.Ordinal).Replace(MajorPlaceholder, "1", StringComparison.Ordinal))
			|| text.Contains('*'))
		{
			error = "versioning.lines.pattern must be a branch name (letters, digits, '/', '.', '_', '-') with {ecosystem} and {major}.";
			return null;
		}
		if (Count(text, EcosystemPlaceholder) != 1 || Count(text, MajorPlaceholder) != 1)
		{
			error = "versioning.lines.pattern must hold {ecosystem} and {major} exactly once each, so every line names its ecosystem (D1).";
			return null;
		}

		var builder = new StringBuilder("^");
		var rest = text;
		while (rest.Length > 0)
		{
			var e = rest.IndexOf(EcosystemPlaceholder, StringComparison.Ordinal);
			var m = rest.IndexOf(MajorPlaceholder, StringComparison.Ordinal);
			var next = e < 0 ? m : m < 0 ? e : Math.Min(e, m);
			if (next < 0) { builder.Append(Regex.Escape(rest)); break; }
			builder.Append(Regex.Escape(rest[..next]));
			if (next == e) { builder.Append("(?<ecosystem>[a-z][a-z0-9]{0,31})"); rest = rest[(next + EcosystemPlaceholder.Length)..]; }
			else { builder.Append("(?<major>[1-9][0-9]{0,8})"); rest = rest[(next + MajorPlaceholder.Length)..]; }
		}
		builder.Append('$');
		return new ReleaseLinePattern(text, new Regex(builder.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
	}

	public static ReleaseLinePattern Parse(string text) => TryParse(text, out var error) ?? throw new VersioningException(error!);

	/// <summary>Whether <paramref name="branch"/> is a release line, and which.</summary>
	public bool TryMatch(string? branch, out ReleaseLine? line)
	{
		line = null;
		if (branch is null) return false;
		var match = regex.Match(branch);
		if (!match.Success) return false;
		line = new ReleaseLine(match.Groups["ecosystem"].Value, int.Parse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture));
		return true;
	}

	/// <summary>The branch of <paramref name="line"/>.</summary>
	public string Format(ReleaseLine line)
		=> Text.Replace(EcosystemPlaceholder, line.Ecosystem, StringComparison.Ordinal).Replace(MajorPlaceholder, line.Major.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

	private static int Count(string text, string placeholder)
	{
		var count = 0;
		for (var i = text.IndexOf(placeholder, StringComparison.Ordinal); i >= 0; i = text.IndexOf(placeholder, i + 1, StringComparison.Ordinal)) count++;
		return count;
	}
}

/// <summary>The ecosystem a package releases in (#568 D1, D19).</summary>
public static class Ecosystems
{
	public const string Dotnet = "dotnet";

	/// <summary>A valid ecosystem name: what a line pattern's <c>{ecosystem}</c> matches.</summary>
	public static bool IsName(string? name)
		=> name is { Length: > 0 and <= 32 } && char.IsAsciiLetterLower(name[0]) && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

	/// <summary>The ecosystem of a frontend framework package (<see cref="MajorAnchor.FrameworkPackages"/>).</summary>
	public static string? OfFramework(string? package) => package switch
	{
		"@angular/core" => "angular",
		"react" => "react",
		"vue" => "vue",
		_ => null,
	};

	/// <summary>
	/// The ecosystem of <paramref name="project"/>: <c>versioning.packages.&lt;id&gt;.line</c> when set; else every NuGet
	/// package is <c>dotnet</c> (anchored or not: netstandard is .NET too), and an npm package is its framework's. A plain
	/// node library has none unless the repository's npm packages all share one framework (D19).
	/// </summary>
	public static string? Of(ProjectGraph graph, ProjectNode project, IReadOnlyDictionary<string, string> pins)
	{
		if (project.Id is { } id && pins.TryGetValue(id, out var pinned)) return pinned;
		if (project.Kind == PackageKind.NuGet) return Dotnet;
		if (OfFramework(project.FrameworkPackage) is { } own) return own;
		var shared = graph.Packages.Where(p => p.Kind == PackageKind.Npm).Select(p => OfFramework(p.FrameworkPackage)).OfType<string>().Distinct().ToList();
		return shared.Count == 1 ? shared[0] : null;
	}
}
