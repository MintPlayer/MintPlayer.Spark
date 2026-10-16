using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.Dependencies;

/// <summary>
/// P12 (dependency-updates PRD §13.8): the dependency graph (M3, §6) also reads what the lookup workflow found. A lookup
/// report covers every ecosystem Renovate's lookup supports (Maven, Gradle, Cargo, Go, … besides M3's six), with the version
/// each repository is on; M3's own scanners stay the fallback for a repository without the workflow, and for every
/// ecosystem the lookup reported nothing of.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is kept:</b> one <see cref="ManifestDependency"/> per (ecosystem, name, package file), with the manifest's value as
/// <see cref="ManifestDependency.Constraint"/> and the resolved version as <see cref="ManifestDependency.Version"/>. Platform
/// records (an SDK, a runner image) are not dependencies between repositories and are left out. Names are normalized the way
/// M3's parsers normalize them (lowercase; PEP 503 for Python; <c>owner/repo</c> for an Action), so producers match.
/// </para>
/// <para>
/// <b>Only consumption.</b> A lookup says what a repository uses, never what it publishes: <see cref="RepositoryManifest.Produces"/>
/// stays the scanners' (§6.1, declared names).
/// </para>
/// <para>
/// <b>Visibility</b> is unchanged (#453): the lookup's packages are stored on the repository's own manifest document, which
/// only the graph and fleet endpoints read, after the same repository filter.
/// </para>
/// </remarks>
public static class DependencyLookupIngest
{
    /// <summary>The most dependencies one lookup stores on a manifest (this repository's lookup holds 504).</summary>
    public const int MaxDependencies = 5000;

    /// <summary>The lookup's records as the graph's dependencies.</summary>
    public static List<ManifestDependency> ToConsumes(LookupDocument lookup)
    {
        var seen = new HashSet<(string, string, string)>();
        var list = new List<ManifestDependency>();
        foreach (var record in lookup.Records)
        {
            if (Ecosystem(record.Ecosystem) is not { } ecosystem || Name(ecosystem, record.PackageName ?? record.DepName) is not { } name)
                continue;
            if (!seen.Add((ecosystem, name, record.PackageFile)))
                continue;
            list.Add(new ManifestDependency
            {
                Ecosystem = ecosystem,
                Name = name,
                Path = record.PackageFile,
                Constraint = record.CurrentValue,
                Version = record.LockedVersion ?? record.CurrentVersion ?? record.CurrentValue,
                Dev = IsDev(record.DepType),
            });
            if (list.Count >= MaxDependencies)
                break;
        }
        return list;
    }

    /// <summary>Stores <paramref name="lookup"/> on <paramref name="manifest"/> (P12).</summary>
    public static void Apply(RepositoryManifest manifest, LookupDocument lookup, DateTimeOffset now)
    {
        manifest.LookupConsumes = ToConsumes(lookup);
        manifest.LookupSha = lookup.ExpectedSha;
        manifest.LookupAt = now;
    }

    /// <summary>
    /// What the graph and the fleet view read as the repository's dependencies: the lookup's when there is one, plus the
    /// scanners' for every ecosystem the lookup reported nothing of; the scanners' alone otherwise.
    /// </summary>
    public static IReadOnlyList<ManifestDependency> Consumes(RepositoryManifest manifest)
    {
        if (manifest.LookupAt is null)
            return manifest.Consumes;
        var covered = manifest.LookupConsumes.Select(d => d.Ecosystem).ToHashSet(StringComparer.Ordinal);
        return [.. manifest.LookupConsumes, .. manifest.Consumes.Where(d => !covered.Contains(d.Ecosystem))];
    }

    /// <summary>The lookup's ecosystem word as the graph's (<see cref="ManifestEcosystems"/> for M3's six); null for
    /// what is not a package another repository could publish.</summary>
    public static string? Ecosystem(string lookupEcosystem) => lookupEcosystem switch
    {
        "" => null,
        "pypi" or "pip" => ManifestEcosystems.Pip,
        "github-actions" => ManifestEcosystems.Actions,
        "github-runners" or "gradle-wrapper" => null,
        _ when lookupEcosystem.StartsWith("platform:", StringComparison.Ordinal) => null,
        _ => lookupEcosystem.ToLowerInvariant(),
    };

    private static string? Name(string ecosystem, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return null;
        switch (ecosystem)
        {
            case ManifestEcosystems.Pip:
                return PipManifestParser.Normalize(name);
            case ManifestEcosystems.Actions:
                var segments = name.Split('/');
                return segments.Length >= 2 && segments[0].Length > 0 && segments[1].Length > 0 ? $"{segments[0]}/{segments[1]}".ToLowerInvariant() : null;
            case ManifestEcosystems.Docker:
                return DockerManifestParser.NormalizeImage(name) ?? name.ToLowerInvariant();
            case ManifestEcosystems.NuGet:
                return name;
            default:
                return name.ToLowerInvariant();
        }
    }

    /// <summary>Renovate's dependency types for development-only use (npm, Composer, Poetry, Cargo, Maven's test scope).</summary>
    private static bool IsDev(string? depType) => depType is { } type
        && (type.Contains("dev", StringComparison.OrdinalIgnoreCase) || type.Contains("test", StringComparison.OrdinalIgnoreCase)
            || type.Equals("build-dependencies", StringComparison.OrdinalIgnoreCase));
}
