using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Validation;

namespace CodeCoverage.Versioning.Releases;

/// <summary>
/// What the action uploads for one run (PRD D5): the commit the workflow built, and per package what only the build
/// knows (its API hash, the target frameworks MSBuild evaluated, what restore resolved) and whether it was packed. The
/// server never trusts it for structure: the graph is parsed statically, and a packed package the graph does not hold
/// fails the run (<see cref="ReleaseRun"/>).
/// </summary>
public sealed record ReleaseManifest
{
	public const int CurrentVersion = 1;
	public const int MaxTargetFrameworks = 32;
	public const int MaxResolvedVersions = 2_000;
	public const int MaxVersionLength = 128;

	public int Version { get; init; } = CurrentVersion;

	/// <summary>The commit the workflow built and the tags go on: a full sha.</summary>
	public string? Commit { get; init; }

	public IReadOnlyList<ManifestPackage>? Packages { get; init; }

	/// <summary>Every rule the manifest breaks, naming the field and never echoing a value; empty when it is well formed.</summary>
	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();
		if (Version != CurrentVersion) errors.Add($"version must be {CurrentVersion}.");
		if (!GitCli.IsObjectId(Commit)) errors.Add("commit must be a full sha of 40 lowercase hex characters.");
		if (Packages is null) errors.Add("packages is required.");
		else if (Packages.Count > Limits.MaxPackagesPerRun) errors.Add($"packages holds more than {Limits.MaxPackagesPerRun} entries.");
		else
		{
			for (var i = 0; i < Packages.Count; i++)
				if (Packages[i]?.Validate() is { } error) errors.Add($"packages[{i}]: {error}");
			if (errors.Count == 0 && Packages.GroupBy(p => (p.Kind, p.Id)).Any(g => g.Count() > 1))
				errors.Add("packages names a package twice.");
		}
		return errors;
	}

	/// <summary>The facts the planner reads, by package id.</summary>
	public IReadOnlyDictionary<string, PackageBuildFacts> BuildFacts()
		=> (Packages ?? []).ToDictionary(p => p.Id!, p => new PackageBuildFacts
		{
			ApiHash = p.ApiHash,
			PreviousApiHash = p.PreviousApiHash,
			TargetFrameworks = p.TargetFrameworks ?? [],
			ResolvedVersions = p.ResolvedVersions ?? new Dictionary<string, string>(),
		}, StringComparer.Ordinal);
}

/// <summary>One package of a <see cref="ReleaseManifest"/>.</summary>
public sealed record ManifestPackage
{
	public string? Id { get; init; }

	/// <summary><c>nuget</c> or <c>npm</c>.</summary>
	public string? Kind { get; init; }

	/// <summary><c>sha256:&lt;64 hex&gt;</c>, or null when the API could not be hashed (D6).</summary>
	public string? ApiHash { get; init; }

	/// <summary>The hash of the last tag's build, when its annotation has none (S7).</summary>
	public string? PreviousApiHash { get; init; }

	public IReadOnlyList<string>? TargetFrameworks { get; init; }

	public IReadOnlyDictionary<string, string>? ResolvedVersions { get; init; }

	/// <summary>The build packed it: it must then be a package of the statically parsed graph (D5).</summary>
	public bool Packed { get; init; }

	[System.Text.Json.Serialization.JsonIgnore]
	public PackageKind? PackageKind => Kind switch
	{
		"nuget" => Graph.PackageKind.NuGet,
		"npm" => Graph.PackageKind.Npm,
		_ => null,
	};

	public static string Wire(PackageKind kind) => kind == Graph.PackageKind.NuGet ? "nuget" : "npm";

	internal string? Validate()
	{
		if (PackageKind is not { } kind) return "kind must be nuget or npm.";
		if (kind == Graph.PackageKind.NuGet ? !Identifiers.IsNuGetId(Id) : !Identifiers.IsNpmName(Id)) return "id is not a valid package id.";
		if (ApiHash is not null && !Identifiers.IsApiHash(ApiHash)) return "apiHash must be sha256:<64 hex>.";
		if (PreviousApiHash is not null && !Identifiers.IsApiHash(PreviousApiHash)) return "previousApiHash must be sha256:<64 hex>.";
		if (TargetFrameworks is { } tfms && (tfms.Count > ReleaseManifest.MaxTargetFrameworks || !tfms.All(Identifiers.IsTargetFramework)))
			return $"targetFrameworks holds at most {ReleaseManifest.MaxTargetFrameworks} target framework monikers.";
		if (ResolvedVersions is { } resolved && (resolved.Count > ReleaseManifest.MaxResolvedVersions
			|| !resolved.All(r => Identifiers.IsPackageId(r.Key) && r.Value is { Length: > 0 and <= ReleaseManifest.MaxVersionLength })))
			return $"resolvedVersions holds at most {ReleaseManifest.MaxResolvedVersions} package ids, each with a version.";
		return null;
	}
}
