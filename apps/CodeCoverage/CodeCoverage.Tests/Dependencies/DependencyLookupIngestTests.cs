using CodeCoverage.Dependencies;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.Tests.Dependencies.Policy;
using Xunit;

namespace CodeCoverage.Tests.Dependencies;

/// <summary>P12: the dependency graph reads the lookup report's package files, with M3's scanners as the fallback.</summary>
public class DependencyLookupIngestTests
{
    private static LookupRecord Record(string ecosystem, string depName, string packageFile, string current, string? depType = null, string? locked = null) => new()
    {
        Ecosystem = ecosystem, Manager = ecosystem, Datasource = ecosystem, DepName = depName, PackageFile = packageFile,
        CurrentValue = current, CurrentVersion = current, LockedVersion = locked, DepType = depType,
    };

    [Fact]
    public void Every_ecosystem_the_lookup_reports_is_kept_with_its_version_and_platforms_are_left_out()
    {
        var lookup = DependencyTestKit.Lookup(
            Record("npm", "@Acme/Lib", "package.json", "^1.2.0", "devDependencies", locked: "1.2.3"),
            Record("maven", "com.acme:core", "pom.xml", "2.0.0"),
            Record("pypi", "Django_REST.framework", "requirements.txt", "==3.15"),
            Record("github-actions", "actions/cache/save", ".github/workflows/ci.yml", "v4"),
            Record("platform:dotnet", "dotnet-sdk", "global.json", "10.0.100"),
            Record("github-runners", "ubuntu", ".github/workflows/ci.yml", "24.04"),
            Record("npm", "@acme/lib", "package.json", "^1.2.0"));

        var consumes = DependencyLookupIngest.ToConsumes(lookup);

        consumes.Select(d => $"{d.Ecosystem}:{d.Name}@{d.Path} {d.Constraint}/{d.Version}{(d.Dev ? " dev" : "")}").Should().Equal([
            "npm:@acme/lib@package.json ^1.2.0/1.2.3 dev",
            "maven:com.acme:core@pom.xml 2.0.0/2.0.0",
            "pip:django-rest-framework@requirements.txt ==3.15/==3.15",
            "actions:actions/cache@.github/workflows/ci.yml v4/v4",
        ]);
    }

    [Fact]
    public void The_lookup_replaces_the_scanners_for_the_ecosystems_it_covers_and_the_scanners_keep_the_rest()
    {
        static Repository Repo(long id, string name) => new()
        {
            Id = Repository.DocumentId(EForgeProvider.GitHub, id), GitHubId = id, Name = name, FullName = $"acme/{name}", OwnerLogin = "acme", DefaultBranch = "main",
        };
        var lib = Repo(1, "lib");
        var app = Repo(2, "app");
        var libManifest = new RepositoryManifest
        {
            Repository = lib.Id, ScannedAt = DependencyTestKit.Now,
            Produces = [new ManifestPackage { Ecosystem = "npm", Name = "@acme/lib", Path = "package.json" }],
        };
        var appManifest = new RepositoryManifest
        {
            Repository = app.Id,
            // The scanners' stale npm reading, and a Dockerfile the lookup did not report.
            Consumes = [new ManifestDependency { Ecosystem = "npm", Name = "@acme/old", Path = "package.json" },
                        new ManifestDependency { Ecosystem = "docker", Name = "ghcr.io/acme/lib", Path = "Dockerfile" }],
        };
        DependencyLookupIngest.Apply(appManifest, DependencyTestKit.Lookup(Record("npm", "@acme/lib", "web/package.json", "^2.0.0")), DependencyTestKit.Now);

        var graph = DependencyGraph.Build([lib, app], new Dictionary<string, RepositoryManifest?> { [lib.Id!] = libManifest, [app.Id!] = appManifest });

        var edge = graph.Edges.Should().ContainSingle().Which;
        edge.Dependencies.Select(d => $"{d.Ecosystem}:{d.Name}@{d.ManifestPath}").Should().Equal(["docker:ghcr.io/acme/lib@Dockerfile", "npm:@acme/lib@web/package.json"]);
        graph.Nodes.Single(n => n.Id == app.Id).ScannedAt.Should().Be(DependencyTestKit.Now, "a repository whose lookup ran counts as scanned");
        appManifest.LookupSha.Should().Be(DependencyTestKit.Sha);
    }
}
