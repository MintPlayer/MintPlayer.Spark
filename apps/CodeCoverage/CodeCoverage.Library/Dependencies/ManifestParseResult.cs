using CodeCoverage.Entities;

namespace CodeCoverage.Dependencies;

/// <summary>The ecosystem names stored on manifests and served by the dependency graph.</summary>
public static class ManifestEcosystems
{
    public const string Npm = "npm";
    public const string NuGet = "nuget";
    public const string Pip = "pip";
    public const string Composer = "composer";
    public const string Actions = "actions";
    public const string Docker = "docker";
}

/// <summary>
/// What one manifest file declares. Parsers never throw: malformed input yields whatever could be
/// read plus <see cref="Error"/>, because one broken file in a repository must not cost the rest of
/// its scan.
/// </summary>
public sealed record ManifestParseResult(
    IReadOnlyList<ManifestPackage> Produces,
    IReadOnlyList<ManifestDependency> Consumes,
    string? Error = null)
{
    public static ManifestParseResult Empty { get; } = new([], []);

    public static ManifestParseResult Failed(string error) => new([], [], error);
}

/// <summary>Collects one manifest's entries, dropping exact duplicates.</summary>
internal sealed class ManifestCollector(string path)
{
    private readonly List<ManifestPackage> produces = [];
    private readonly List<ManifestDependency> consumes = [];
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

    public void Produce(string ecosystem, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (seen.Add($"p\n{ecosystem}\n{name}"))
            produces.Add(new ManifestPackage { Ecosystem = ecosystem, Name = name, Path = path });
    }

    public void Consume(string ecosystem, string name, string? constraint, bool dev)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        constraint = string.IsNullOrWhiteSpace(constraint) ? null : constraint.Trim();
        if (seen.Add($"c\n{ecosystem}\n{name}\n{constraint}\n{dev}"))
            consumes.Add(new ManifestDependency { Ecosystem = ecosystem, Name = name, Path = path, Constraint = constraint, Dev = dev });
    }

    public ManifestParseResult Result(string? error = null) => new(produces, consumes, error);
}
