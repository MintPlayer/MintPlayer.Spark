using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Versions;

/// <summary>
/// Any semver 2 version, as upstream packages publish them (<c>22.0.0-next.3</c>, <c>19.0.0-canary-ab12</c>,
/// <c>11.0.0-preview.7.24407.12</c>). Only read, never written: our own versions are <see cref="PackageVersion"/>.
/// Comparison follows semver 2 precedence; build metadata is dropped.
/// </summary>
public sealed partial record SemVer(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemVer>
{
	[GeneratedRegex(@"^v?(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
	private static partial Regex Grammar();

	public static bool TryParse(string? text, out SemVer version)
	{
		version = null!;
		if (text is null || text.Length > 128) return false;
		var match = Grammar().Match(text.Trim());
		if (!match.Success) return false;

		static int Int(Group g) => int.Parse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture);
		version = new SemVer(Int(match.Groups[1]), Int(match.Groups[2]), Int(match.Groups[3]),
			match.Groups[4].Success ? match.Groups[4].Value : null);
		return true;
	}

	public bool SameCore(SemVer other) => Major == other.Major && Minor == other.Minor && Patch == other.Patch;

	public int CompareTo(SemVer? other)
	{
		if (other is null) return 1;
		var c = Major.CompareTo(other.Major);
		if (c == 0) c = Minor.CompareTo(other.Minor);
		if (c == 0) c = Patch.CompareTo(other.Patch);
		if (c != 0) return c;
		if (Prerelease is null || other.Prerelease is null) return (Prerelease is null).CompareTo(other.Prerelease is null);

		var mine = Prerelease.Split('.');
		var theirs = other.Prerelease.Split('.');
		for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
		{
			var aNumeric = long.TryParse(mine[i], NumberStyles.None, CultureInfo.InvariantCulture, out var a);
			var bNumeric = long.TryParse(theirs[i], NumberStyles.None, CultureInfo.InvariantCulture, out var b);
			c = (aNumeric, bNumeric) switch
			{
				(true, true) => a.CompareTo(b),
				(true, false) => -1,
				(false, true) => 1,
				_ => string.CompareOrdinal(mine[i], theirs[i]),
			};
			if (c != 0) return Math.Sign(c);
		}
		return mine.Length.CompareTo(theirs.Length);
	}

	public override string ToString() => Prerelease is null
		? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
		: string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}");
}
