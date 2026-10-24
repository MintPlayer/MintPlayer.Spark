using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Validation;

/// <summary>
/// The grammar checks of PRD D5 for text that becomes part of a tag, a ref or an annotation. Everything here is read
/// from a consumer's repository or an uploaded manifest, so it is untrusted: each check is an anchored pattern over a
/// small alphabet (no backtracking blow-up), with <c>\z</c> rather than <c>$</c> so a trailing newline cannot slip
/// through, and <c>[0-9]</c> rather than <c>\d</c>, which would admit every Unicode digit.
/// </summary>
public static partial class Identifiers
{
	public const int MaxNuGetIdLength = 100;
	public const int MaxNpmNameLength = 214;

	[GeneratedRegex(@"^[A-Za-z0-9_]+(?:[.-][A-Za-z0-9_]+)*\z", RegexOptions.CultureInvariant)]
	private static partial Regex NuGetIdGrammar();

	[GeneratedRegex(@"^(?:@[a-z0-9~-][a-z0-9._~-]*/)?[a-z0-9~-][a-z0-9._~-]*\z", RegexOptions.CultureInvariant)]
	private static partial Regex NpmNameGrammar();

	[GeneratedRegex(@"^sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
	private static partial Regex ApiHashGrammar();

	[GeneratedRegex(@"^(?:net[0-9]+\.[0-9]+(?:-[a-z]+(?:[0-9]+(?:\.[0-9]+){0,3})?)?|net[0-9]{2,3}|netcoreapp[0-9]+\.[0-9]+|netstandard[0-9]+\.[0-9]+)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex TargetFrameworkGrammar();

	/// <summary>A NuGet package id: letters, digits and underscores in segments joined by <c>.</c> or <c>-</c>.</summary>
	public static bool IsNuGetId(string? id) => id is { Length: > 0 and <= MaxNuGetIdLength } && NuGetIdGrammar().IsMatch(id);

	/// <summary>An npm package name, scoped or not, lower case, url-safe, not starting with <c>.</c> or <c>_</c>.</summary>
	public static bool IsNpmName(string? name) => name is { Length: > 0 and <= MaxNpmNameLength } && NpmNameGrammar().IsMatch(name)
		&& name.Split('/')[^1] is not ("node_modules" or "favicon.ico");

	public static bool IsPackageId(string? id) => IsNuGetId(id) || IsNpmName(id);

	public static bool IsApiHash(string? hash) => hash is not null && ApiHashGrammar().IsMatch(hash);

	public static bool IsTargetFramework(string? tfm) => tfm is { Length: > 0 and <= 64 } && TargetFrameworkGrammar().IsMatch(tfm);

	/// <summary>
	/// <c>git check-ref-format</c>'s rules for a name below <c>refs/tags/</c> or <c>refs/heads/</c>, so a name git would
	/// refuse, or read as something other than a plain ref, never reaches a git command line.
	/// </summary>
	public static bool IsRefName(string? name)
	{
		if (string.IsNullOrEmpty(name) || name.Length > 255 || name == "@") return false;
		if (name.EndsWith('/') || name.EndsWith('.') || name.StartsWith('/') || name.StartsWith('-')) return false;
		if (name.Contains("..") || name.Contains("//") || name.Contains("@{")) return false;
		foreach (var c in name)
			if (c < 0x20 || c == 0x7f || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\') return false;
		foreach (var component in name.Split('/'))
			if (component.StartsWith('.') || component.EndsWith(".lock", StringComparison.Ordinal)) return false;
		return true;
	}
}
