using System.Text.Json;
using CodeCoverage.Controllers;
using CodeCoverage.Dependencies;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Dependencies;

/// <summary>
/// <c>GET api/browse/accounts/{provider}/{login}/dependency-graph</c>: the account's repositories
/// as nodes, producer → consumer edges between them, and the #453 rule that a repository the viewer
/// cannot see contributes nothing — not a node, not an edge end, not a package name.
/// </summary>
public class DependencyGraphEndpointTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long Lib = 9401;
    private const long Secret = 9402;
    private const long App = 9403;
    private const long Unscanned = 9404;
    private const long Foreign = 9405;

    private static string RepoId(long id) => Repository.DocumentId(EForgeProvider.GitHub, id);

    private static BrowseController CreateController(IAsyncDocumentSession session, params string[] owners)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        var forge = ScriptedForge.From(new(owners, GitHubTokenState.Ok));
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton(GitHubAuthTestFakes.TestConfiguration());
        services.AddScoped<IRepositoryResolver>(sp => new TestRepositoryResolver(sp.GetService<IAsyncDocumentSession>()));
        services.AddScoped<BrowseController>();
        var provider = services.BuildServiceProvider();
        var controller = provider.GetRequiredService<BrowseController>();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = provider } };
        return controller;
    }

    private static Repository Repo(long id, string owner, string name, bool isPrivate) => new()
    {
        GitHubId = id, Name = name, FullName = $"{owner}/{name}", OwnerLogin = owner, IsPrivate = isPrivate, DefaultBranch = "main",
    };

    private static ManifestDependency Uses(string ecosystem, string name, string path, bool dev = false)
        => new() { Ecosystem = ecosystem, Name = name, Path = path, Dev = dev };

    private static ManifestPackage Makes(string ecosystem, string name, string path)
        => new() { Ecosystem = ecosystem, Name = name, Path = path };

    private static readonly DateTime ScannedAt = new(2026, 10, 9, 3, 20, 0, DateTimeKind.Utc);

    /// <summary>
    /// acme/lib publishes @acme/lib; the private acme/secret consumes it and publishes Acme.Secret;
    /// acme/app consumes both, plus lib's action; globex/foreign consumes @acme/lib from another account.
    /// </summary>
    private static async Task SeedAsync(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        foreach (var (id, repository) in new[]
                 {
                     (Lib, Repo(Lib, "acme", "lib", isPrivate: false)),
                     (Secret, Repo(Secret, "acme", "secret", isPrivate: true)),
                     (App, Repo(App, "acme", "app", isPrivate: false)),
                     (Unscanned, Repo(Unscanned, "acme", "unscanned", isPrivate: false)),
                     (Foreign, Repo(Foreign, "globex", "foreign", isPrivate: false)),
                 })
            await seed.StoreAsync(repository, RepoId(id));

        await seed.StoreAsync(new RepositoryManifest
        {
            Repository = RepoId(Lib), ScannedAt = ScannedAt,
            Produces = [Makes("npm", "@acme/lib", "package.json")],
            // Its own package, consumed by its own demo: never an edge to itself.
            Consumes = [Uses("npm", "@acme/lib", "demo/package.json")],
        }, RepositoryManifest.ForRepository(RepoId(Lib)));
        await seed.StoreAsync(new RepositoryManifest
        {
            Repository = RepoId(Secret), ScannedAt = ScannedAt,
            Produces = [Makes("nuget", "Acme.Secret", "src/Acme.Secret.csproj")],
            Consumes = [Uses("npm", "@acme/lib", "web/package.json")],
        }, RepositoryManifest.ForRepository(RepoId(Secret)));
        await seed.StoreAsync(new RepositoryManifest
        {
            Repository = RepoId(App), ScannedAt = ScannedAt, ScanError = "1 of 3 manifest files could not be read.",
            Consumes =
            [
                Uses("npm", "@acme/lib", "package.json"),
                Uses("npm", "@acme/lib", "e2e/package.json", dev: true),
                Uses("nuget", "acme.secret", "src/App.csproj"),
                Uses("actions", "acme/lib", ".github/workflows/ci.yml"),
                Uses("npm", "left-pad", "package.json"),
            ],
        }, RepositoryManifest.ForRepository(RepoId(App)));
        await seed.StoreAsync(new RepositoryManifest
        {
            Repository = RepoId(Foreign), ScannedAt = ScannedAt,
            Consumes = [Uses("npm", "@acme/lib", "package.json")],
        }, RepositoryManifest.ForRepository(RepoId(Foreign)));

        await seed.SaveChangesAsync();
    }

    private static DependencyGraphResponse Ok(ActionResult<DependencyGraphResponse> result)
        => (DependencyGraphResponse)((OkObjectResult)result.Result!).Value!;

    private static string Edge(DependencyGraphEdge edge)
        => $"{edge.From} -> {edge.To}: " + string.Join("; ", edge.Dependencies.Select(d => $"{d.Ecosystem}:{d.Name}@{d.ManifestPath}{(d.Dev ? " (dev)" : "")}"));

    [Fact]
    public async Task An_anonymous_viewer_sees_no_trace_of_the_private_repository()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var graph = Ok(await CreateController(session).GetDependencyGraph("github", "acme", default));

        graph.Nodes.Select(n => n.FullName).Should().Equal("acme/app", "acme/lib", "acme/unscanned");
        graph.Edges.Select(Edge).Should().Equal(
            $"{RepoId(Lib)} -> {RepoId(App)}: actions:acme/lib@.github/workflows/ci.yml; npm:@acme/lib@e2e/package.json (dev); npm:@acme/lib@package.json");
        // Not even a package name of the hidden repository survives into a visible edge.
        graph.Edges.SelectMany(e => e.Dependencies).Any(d => d.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)).Should().BeFalse();
    }

    [Fact]
    public async Task An_owner_sees_the_private_repository_and_its_edges()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var graph = Ok(await CreateController(session, "acme").GetDependencyGraph("github", "acme", default));

        graph.Nodes.Select(n => n.FullName).Should().Equal("acme/app", "acme/lib", "acme/secret", "acme/unscanned");
        graph.Edges.Select(e => $"{e.From} -> {e.To}").Should().Equal(
            $"{RepoId(Lib)} -> {RepoId(Secret)}",
            $"{RepoId(Lib)} -> {RepoId(App)}",
            $"{RepoId(Secret)} -> {RepoId(App)}");
        // NuGet ids compare case-insensitively.
        graph.Edges.Single(e => e.From == RepoId(Secret)).Dependencies.Should().ContainSingle().Which.Name.Should().Be("acme.secret");

        var app = graph.Nodes.Single(n => n.Name == "app");
        app.ScanError.Should().Be("1 of 3 manifest files could not be read.");
        app.ScannedAt!.Value.Should().Be(ScannedAt);
        graph.Nodes.Single(n => n.Name == "unscanned").ScannedAt.HasValue.Should().BeFalse();
        graph.Nodes.Single(n => n.Name == "secret").IsPrivate.Should().BeTrue();
    }

    [Fact]
    public async Task Edges_never_cross_accounts()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var globex = Ok(await CreateController(session, "acme", "globex").GetDependencyGraph("github", "globex", default));

        globex.Nodes.Select(n => n.FullName).Should().Equal("globex/foreign");
        globex.Edges.Should().BeEmpty();
    }

    /// <summary>Same refusal as the account's repository list: an unknown forge is the shared denial, an unknown login an empty graph.</summary>
    [Fact]
    public async Task An_unknown_forge_is_refused_and_an_unknown_account_is_empty()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        (await controller.GetDependencyGraph("nosuchforge", "acme", default)).Result.Should().BeOfType<JsonResult>();
        var missing = Ok(await controller.GetDependencyGraph("github", "nobody", default));
        missing.Nodes.Should().BeEmpty();
        missing.Edges.Should().BeEmpty();
    }

    /// <summary>The wire contract the Angular card binds to, property for property.</summary>
    [Fact]
    public async Task The_response_serializes_to_the_agreed_camel_case_contract()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        var graph = Ok(await CreateController(session).GetDependencyGraph("github", "acme", default));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().Equal("nodes", "edges");
        var node = root.GetProperty("nodes")[0];
        node.EnumerateObject().Select(p => p.Name).Should().Equal("id", "fullName", "name", "isPrivate", "archived", "scannedAt", "scanError");
        node.GetProperty("id").GetString().Should().Be(RepoId(App));
        node.GetProperty("scannedAt").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("nodes")[2].GetProperty("scannedAt").ValueKind.Should().Be(JsonValueKind.Null);

        var edge = root.GetProperty("edges")[0];
        edge.EnumerateObject().Select(p => p.Name).Should().Equal("from", "to", "dependencies");
        edge.GetProperty("dependencies")[0].EnumerateObject().Select(p => p.Name).Should().Equal("ecosystem", "name", "manifestPath", "dev");
    }

    // ── The builder on its own ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_sub_image_is_built_by_the_repository_it_is_named_under_and_actions_match_by_repository()
    {
        var producer = new Repository { Id = RepoId(1), Name = "Platform", FullName = "Acme/Platform" };
        var consumer = new Repository { Id = RepoId(2), Name = "svc", FullName = "acme/svc" };
        var manifests = new Dictionary<string, RepositoryManifest?>
        {
            [RepoId(1)] = null,
            [RepoId(2)] = new()
            {
                Consumes =
                [
                    Uses("docker", "ghcr.io/acme/platform/worker", "Dockerfile"),
                    Uses("docker", "ghcr.io/acme/platformer", "compose.yml"),
                    Uses("actions", "acme/platform", ".github/workflows/ci.yml"),
                ],
            },
        };

        var graph = DependencyGraph.Build([producer, consumer], manifests);

        var edge = graph.Edges.Should().ContainSingle().Which;
        edge.From.Should().Be(RepoId(1));
        edge.To.Should().Be(RepoId(2));
        edge.Dependencies.Select(d => d.Name).Should().Equal("acme/platform", "ghcr.io/acme/platform/worker");
    }
}
