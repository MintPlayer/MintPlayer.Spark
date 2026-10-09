using CodeCoverage.Forge;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// What one repository's default branch produces and consumes, read from the package manifests
/// anywhere in its tree (dependency-updates PRD §6). One document per repository, written only by
/// the manifest scanner and read by the account dependency graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>A plain document, deliberately not a persistent object.</b> It is not in
/// <c>CoverageSparkContext</c> and has no model JSON, so <c>/spark</c> never serves it and
/// security.json has nothing to grant. The only reader is the dependency-graph endpoint, which
/// applies the repository visibility rule itself (#453) — a manifest is exactly as private as the
/// repository it describes, and serving it through a second path would be a second rule to keep in
/// step.
/// </para>
/// <para>
/// <b>The id is derived from the repository id</b> (<see cref="ForRepository"/>), so the graph loads
/// every manifest of an account in one round trip by id, with no index.
/// </para>
/// </remarks>
public class RepositoryManifest
{
    /// <summary>
    /// Bumped whenever a parser changes what it extracts. A manifest scanned by an older version is
    /// re-read even though its tree is unchanged — otherwise a parser fix would never reach a
    /// repository nobody pushes to.
    /// </summary>
    public const int CurrentParserVersion = 1;

    /// <summary><c>RepositoryManifests/{provider}/{repositoryId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>The repository this describes.</summary>
    [Reference(typeof(Repository))]
    public string? Repository { get; set; }

    /// <summary>The repository's owner key at scan time, for diagnostics; the graph filters on the repository itself.</summary>
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>
    /// The git tree that was scanned. A scan whose tree is unchanged stops before fetching any file.
    /// Null after a partial failure, so the next trigger retries instead of trusting the partial read.
    /// </summary>
    public string? TreeSha { get; set; }

    /// <summary>The parser version that produced <see cref="Produces"/> and <see cref="Consumes"/>.</summary>
    public int ParserVersion { get; set; }

    /// <summary>
    /// When the last scan attempt finished, successful or not, in UTC. A plain <see cref="DateTime"/>:
    /// <c>Commit</c> is the only entity allowed a <see cref="DateTimeOffset"/>
    /// (<c>CommitIndexShapeGuardTests</c>).
    /// </summary>
    public DateTime? ScannedAt { get; set; }

    /// <summary>Why the last scan could not complete, fit to show the owner. Null when it did.</summary>
    public string? ScanError { get; set; }

    /// <summary>
    /// True when the forge truncated the tree listing or the repository holds more manifests than one
    /// scan reads (<c>RepositoryManifestScanner.MaxManifestFiles</c>): the lists are then a subset.
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>Package ids this repository publishes, as declared by its manifests.</summary>
    public List<ManifestPackage> Produces { get; set; } = [];

    /// <summary>Package ids this repository references.</summary>
    public List<ManifestDependency> Consumes { get; set; } = [];

    /// <summary>Manifests that could not be parsed, as <c>path: reason</c>. Diagnostics only.</summary>
    public List<string> ParseErrors { get; set; } = [];

    /// <summary><c>RepositoryManifests/{provider}/{repositoryId}</c>.</summary>
    public static string DocumentId(EForgeProvider provider, long repositoryId)
        => $"RepositoryManifests/{provider.ToCanonicalString()}/{repositoryId}";

    /// <summary>
    /// The manifest id for a repository document id: <c>Repositories/github/1</c> becomes
    /// <c>RepositoryManifests/github/1</c>.
    /// </summary>
    public static string ForRepository(string repositoryId)
    {
        const string prefix = "Repositories/";
        if (!repositoryId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return "RepositoryManifests/" + repositoryId[prefix.Length..];
    }
}

/// <summary>A package a repository publishes.</summary>
public class ManifestPackage
{
    /// <summary>One of <see cref="Dependencies.ManifestEcosystems"/>.</summary>
    public string Ecosystem { get; set; } = string.Empty;

    /// <summary>The package id, normalized per ecosystem (see the parsers).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The manifest that declares it, relative to the repository root.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>A package a repository references.</summary>
public class ManifestDependency
{
    /// <summary>One of <see cref="Dependencies.ManifestEcosystems"/>.</summary>
    public string Ecosystem { get; set; } = string.Empty;

    /// <summary>The package id, normalized per ecosystem (see the parsers).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The manifest that references it, relative to the repository root.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The version range or ref as written, when the manifest states one.</summary>
    public string? Constraint { get; set; }

    /// <summary>Development-only: a dev dependency, a test project's reference, a private asset.</summary>
    public bool Dev { get; set; }
}
