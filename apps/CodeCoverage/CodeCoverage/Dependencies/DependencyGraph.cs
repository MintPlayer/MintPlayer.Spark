using CodeCoverage.Entities;

namespace CodeCoverage.Dependencies;

/// <summary>The body of <c>GET api/browse/accounts/{provider}/{login}/dependency-graph</c>.</summary>
public sealed record DependencyGraphResponse(IReadOnlyList<DependencyGraphNode> Nodes, IReadOnlyList<DependencyGraphEdge> Edges);

/// <summary>One repository. <see cref="ScannedAt"/> is null until its manifests were first scanned.</summary>
public sealed record DependencyGraphNode(
    string Id, string FullName, string Name, bool IsPrivate, bool Archived, DateTime? ScannedAt, string? ScanError);

/// <summary><see cref="From"/> produces packages that <see cref="To"/> consumes.</summary>
public sealed record DependencyGraphEdge(string From, string To, IReadOnlyList<DependencyGraphDependency> Dependencies);

/// <summary>One package carrying an edge, and the consumer's manifest that references it.</summary>
public sealed record DependencyGraphDependency(string Ecosystem, string Name, string ManifestPath, bool Dev);

/// <summary>
/// Builds an account's repository dependency graph from its repositories and their manifests
/// (dependency-updates PRD §6). Pure: the caller decides which repositories the viewer may see.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Only the repositories passed in exist.</b> Producers and consumers are both drawn from
/// <paramref name="repositories"/>, so a repository the viewer cannot see contributes neither a node
/// nor an edge nor a package name — the #453 rule that missing is the same as no access applies to
/// the edges too, not just the node list.
/// </para>
/// <para>
/// Every repository implicitly produces the GitHub Action and the ghcr.io image named after it,
/// derived here from its current name rather than stored, so a rename takes effect at once.
/// </para>
/// <para>
/// Package ids compare case-insensitively: NuGet ids are case-insensitive, and the other ecosystems'
/// parsers already lowercase or normalize. A consumed image matches a produced one exactly or as a
/// sub-image (<c>ghcr.io/acme/app/worker</c> is built by <c>acme/app</c>).
/// </para>
/// </remarks>
public static class DependencyGraph
{
    public static DependencyGraphResponse Build(
        IReadOnlyCollection<Repository> repositories,
        IReadOnlyDictionary<string, RepositoryManifest?> manifestsByRepositoryId)
    {
        var nodes = repositories
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r =>
            {
                var manifest = manifestsByRepositoryId.GetValueOrDefault(r.Id!);
                return new DependencyGraphNode(r.Id!, r.FullName, r.Name, r.IsPrivate, r.Archived, manifest?.ScannedAt, manifest?.ScanError);
            })
            .ToList();

        // (ecosystem, name) -> producing repository ids.
        var producers = new Dictionary<(string Ecosystem, string Name), HashSet<string>>(PackageKeyComparer.Instance);
        void AddProducer(string ecosystem, string name, string repositoryId)
        {
            var key = (ecosystem, name);
            if (!producers.TryGetValue(key, out var set)) producers[key] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(repositoryId);
        }

        foreach (var repository in repositories)
        {
            AddProducer(ManifestEcosystems.Actions, ActionsManifestParser.ProducedName(repository.FullName), repository.Id!);
            AddProducer(ManifestEcosystems.Docker, DockerManifestParser.ProducedName(repository.FullName), repository.Id!);
            if (manifestsByRepositoryId.GetValueOrDefault(repository.Id!) is { } manifest)
                foreach (var package in manifest.Produces)
                    AddProducer(package.Ecosystem, package.Name, repository.Id!);
        }

        var edges = new Dictionary<(string From, string To), HashSet<DependencyGraphDependency>>();
        foreach (var consumer in repositories)
        {
            if (manifestsByRepositoryId.GetValueOrDefault(consumer.Id!) is not { } manifest) continue;

            foreach (var dependency in manifest.Consumes)
            {
                foreach (var producer in ProducersOf(producers, dependency))
                {
                    if (producer == consumer.Id) continue;
                    var key = (producer, consumer.Id!);
                    if (!edges.TryGetValue(key, out var set)) edges[key] = set = [];
                    set.Add(new DependencyGraphDependency(dependency.Ecosystem, dependency.Name, dependency.Path, dependency.Dev));
                }
            }
        }

        var edgeList = edges
            .OrderBy(e => e.Key.From, StringComparer.Ordinal)
            .ThenBy(e => e.Key.To, StringComparer.Ordinal)
            .Select(e => new DependencyGraphEdge(e.Key.From, e.Key.To, e.Value
                .OrderBy(d => d.Ecosystem, StringComparer.Ordinal)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.ManifestPath, StringComparer.Ordinal)
                .ThenBy(d => d.Dev)
                .ToList()))
            .ToList();

        return new DependencyGraphResponse(nodes, edgeList);
    }

    private static IEnumerable<string> ProducersOf(
        Dictionary<(string Ecosystem, string Name), HashSet<string>> producers, ManifestDependency dependency)
    {
        if (producers.TryGetValue((dependency.Ecosystem, dependency.Name), out var exact))
            foreach (var id in exact) yield return id;

        if (dependency.Ecosystem != ManifestEcosystems.Docker) yield break;

        // ghcr.io/owner/repo/sub-image: try each shorter prefix on a '/' boundary.
        var name = dependency.Name;
        for (var slash = name.LastIndexOf('/'); slash > 0; slash = name.LastIndexOf('/', slash - 1))
        {
            if (producers.TryGetValue((dependency.Ecosystem, name[..slash]), out var prefix))
                foreach (var id in prefix) yield return id;
        }
    }

    private sealed class PackageKeyComparer : IEqualityComparer<(string Ecosystem, string Name)>
    {
        public static PackageKeyComparer Instance { get; } = new();

        public bool Equals((string Ecosystem, string Name) x, (string Ecosystem, string Name) y)
            => string.Equals(x.Ecosystem, y.Ecosystem, StringComparison.Ordinal)
               && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Ecosystem, string Name) obj)
            => HashCode.Combine(obj.Ecosystem, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
