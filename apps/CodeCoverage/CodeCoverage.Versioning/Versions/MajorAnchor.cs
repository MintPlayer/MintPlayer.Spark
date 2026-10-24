using System.Globalization;
using CodeCoverage.Versioning.Validation;

namespace CodeCoverage.Versioning.Versions;

/// <summary>
/// Where a package's major comes from (PRD D7): the platform it targets, or nothing (<see cref="IsFree"/>), in which
/// case the owner sets the major by pushing a tag. <see cref="Source"/> says why, for the check run.
/// </summary>
public sealed record MajorAnchor(int? Major, string Source)
{
	/// <summary>The frontend frameworks whose resolved major is a library's major, in the order they are looked for.</summary>
	public static readonly IReadOnlyList<string> FrameworkPackages = ["@angular/core", "react", "vue"];

	public bool IsFree => Major is null;

	public static MajorAnchor Free(string source) => new(null, source);

	/// <summary>
	/// The anchor of a .NET project from its evaluated target frameworks: the highest <c>netX.Y</c> with X ≥ 5 (a
	/// platform suffix such as <c>-windows</c> is ignored), else the highest <c>netcoreappX.Y</c>; free when only
	/// <c>netstandard*</c> or .NET Framework (<c>net48</c>) is targeted. A string that is not a target framework is
	/// refused: it came from a consumer's csproj or manifest.
	/// </summary>
	public static MajorAnchor FromTargetFrameworks(IEnumerable<string> targetFrameworks)
	{
		int? net = null, core = null;
		var any = false;
		foreach (var raw in targetFrameworks)
		{
			var tfm = raw.Trim().ToLowerInvariant();
			if (tfm.Length == 0) continue;
			if (!Identifiers.IsTargetFramework(tfm)) throw new VersioningException($"'{raw}' is not a target framework.");
			any = true;

			if (tfm.StartsWith("netcoreapp", StringComparison.Ordinal))
				core = Max(core, MajorOf(tfm["netcoreapp".Length..]));
			else if (!tfm.StartsWith("netstandard", StringComparison.Ordinal) && tfm.Contains('.'))
			{
				// "net5.0" and later carry a dot; .NET Framework monikers ("net48", "net472") never do.
				var major = MajorOf(tfm[3..]);
				if (major >= 5) net = Max(net, major);
			}
		}
		if (!any) throw new VersioningException("The project has no target framework.");
		if (net is { } n) return new(n, $"targets net{n}.0");
		if (core is { } c) return new(c, $"targets netcoreapp{c}.x");
		return Free("targets only .NET Standard or .NET Framework");
	}

	/// <summary>
	/// The anchor of a frontend library: the major of the framework version resolved in the lockfile. The library's
	/// peer range on that framework, when it declares one, must admit the resolved version, or the release would claim
	/// a major its consumers cannot install with.
	/// </summary>
	public static MajorAnchor FromFramework(string package, string resolvedVersion, string? peerRange)
	{
		if (!SemVer.TryParse(resolvedVersion, out var resolved))
			throw new VersioningException($"The lockfile resolves {package} to '{resolvedVersion}', which is not a version.");
		if (peerRange is not null)
		{
			if (!NpmRange.TryParse(peerRange, out var range))
				throw new VersioningException($"The peer dependency range '{peerRange}' on {package} is not a range the engine can read.");
			if (!range.Admits(resolved))
				throw new VersioningException($"The peer dependency range '{peerRange}' on {package} does not include the resolved {resolved}; widen it before releasing.");
		}
		return new(resolved.Major, $"{package} resolves to {resolved}");
	}

	/// <summary>
	/// The anchor set by <c>versioning.packages.&lt;id&gt;.anchor</c> in coverage.yml: <c>free</c>, or the package whose
	/// resolved major the package follows. <paramref name="resolvedVersion"/> is that package's version from the
	/// lockfile or the release manifest, null when the package is not a dependency at all.
	/// </summary>
	public static MajorAnchor FromOverride(string setting, string? resolvedVersion)
	{
		if (setting == "free") return Free("versioning.packages.<id>.anchor: free");
		if (!Identifiers.IsPackageId(setting))
			throw new VersioningException($"The anchor '{setting}' is neither 'free' nor a package id.");
		if (resolvedVersion is null)
			throw new VersioningException($"The anchor {setting} is not a resolved dependency of the package.");
		if (!SemVer.TryParse(resolvedVersion, out var resolved))
			throw new VersioningException($"The anchor {setting} resolves to '{resolvedVersion}', which is not a version.");
		return new(resolved.Major, $"anchored to {setting} {resolved}");
	}

	private static int MajorOf(string version) => int.Parse(version.Split('.', '-')[0], NumberStyles.None, CultureInfo.InvariantCulture);

	private static int? Max(int? a, int b) => a is null || b > a ? b : a;
}
