using System.Security.Claims;
using System.Text.Json;
using CodeCoverage.Badges;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;
using Account = CodeCoverage.Entities.Account;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// #453: "this repository does not exist" and "it exists, but you may not see it" must be
/// indistinguishable — in status, body and headers, and in the work done to answer.
/// <para>
/// Run against the REAL <see cref="RepositoryResolver"/>, not <see cref="TestRepositoryResolver"/>:
/// the oracle this closes lived in the resolver (a private name returned at step one in ~4 ms, a
/// missing one paid a GitHub round trip), so a stub would prove the refusal shape and nothing
/// about the path. The installation service counts calls and then throws, which the resolver
/// treats as "GitHub unavailable" — so every variant ends as a miss, and the count says whether
/// the variant got as far as asking.
/// </para>
/// </summary>
public class RepositoryExistenceParityTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long PrivateRepoId = 4530;
    private const long PublicRepoId = 4531;

    private sealed class NullContentService : IGitHubContentService
    {
        public Task<string?> GetFileContentAsync(Repository repository, long? installationId, string sha, string path, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// The variants a prober would try. Every one must answer like every other; the first two name
    /// a private repository that really exists (by its live name and by a name it used to have).
    /// </summary>
    public static TheoryData<string, string, string> Variants => new()
    {
        { "github", "acme", "secret" },          // private, live name
        { "github", "acme", "old-secret" },      // private, remembered alias
        { "github", "acme", "made-up" },         // missing, under a known owner
        { "github", "stranger", "anything" },    // unknown owner
        { "nosuchforge", "acme", "secret" },     // unknown forge
    };

    private async Task<IDocumentStore> SeedAsync(IDocumentStore store)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(
                new Account { GitHubId = 5, Provider = EForgeProvider.GitHub, Login = "acme", Type = "Organization" },
                Account.DocumentId(EForgeProvider.GitHub, 5));
            await seed.StoreAsync(new Repository
            {
                GitHubId = PrivateRepoId,
                Provider = EForgeProvider.GitHub,
                Name = "secret",
                FullName = "acme/secret",
                OwnerLogin = "acme",
                IsPrivate = true,
                BadgeToken = "the-real-badge-token",
                PreviousFullNames = ["acme/old-secret"],
                LatestCoverage = new CoverageSummary { LinesCovered = 9, LinesCoverable = 10 },
            }, Repository.DocumentId(EForgeProvider.GitHub, PrivateRepoId));
            await seed.StoreAsync(new Repository
            {
                GitHubId = PublicRepoId,
                Provider = EForgeProvider.GitHub,
                Name = "open",
                FullName = "acme/open",
                OwnerLogin = "acme",
                IsPrivate = false,
            }, Repository.DocumentId(EForgeProvider.GitHub, PublicRepoId));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);
        return store;
    }

    /// <summary>A fresh container per call, so the resolver's memory cache never carries a variant's answer into the next.</summary>
    private static (IServiceProvider Services, StubGitHub GitHub) CreateServices(IAsyncDocumentSession session)
    {
        // Counts the app-client requests; there is no GitHub in these tests, so each one throws.
        var github = new StubGitHub { Throws = new InvalidOperationException("no GitHub in tests") };
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddMemoryCache();
        services.AddSingleton(session);
        services.AddSingleton<IGitHubInstallationService>(github);
        services.AddScoped<IRepositoryResolver, RepositoryResolver>();

        // A signed-in caller who administers nothing: the "authenticated non-member".
        var forge = ScriptedForge.From(new([], GitHubTokenState.Ok));
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton<IGitHubAccessService>(new ScriptedAccessService(new([], GitHubTokenState.Ok)));
        services.AddSingleton<IGitHubContentService>(new NullContentService());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [BadgePrSignature.KeyConfigurationPath] = "test-badge-signing-key" }).Build());

        services.AddScoped<BrowseController>();
        services.AddScoped<BadgeController>();
        services.AddScoped<RepoSettingsController>();
        return (services.BuildServiceProvider(), github);
    }

    private static T Controller<T>(IServiceProvider services, bool authenticated) where T : ControllerBase
    {
        var controller = services.GetRequiredService<T>();
        var user = authenticated
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "outsider")], "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user, RequestServices = services },
        };
        return controller;
    }

    private static string Describe(ActionResult result) => result switch
    {
        JsonResult json => $"{json.StatusCode} {JsonSerializer.Serialize(json.Value, JsonSerializerOptions.Web)}",
        ObjectResult obj => $"{obj.StatusCode} {JsonSerializer.Serialize(obj.Value, JsonSerializerOptions.Web)}",
        StatusCodeResult status => $"{status.StatusCode}",
        _ => result.GetType().Name,
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Browse_refuses_every_variant_with_the_canonical_refusal(string provider, string owner, string name)
    {
        using var store = await SeedAsync(GetDocumentStore());

        foreach (var authenticated in new[] { false, true })
        {
            using var session = store.OpenAsyncSession();
            var (services, _) = CreateServices(session);

            var result = (await Controller<BrowseController>(services, authenticated)
                .GetRepo(provider, owner, name, CancellationToken.None)).Result!;

            // The same answer /spark/po/load gives — status AND body, so the SPA's sign-in prompt
            // behaves identically and no field says which case this was.
            Describe(result).Should().Be(
                authenticated ? """404 {"error":"Not found"}""" : """401 {"error":"Authentication required"}""",
                $"{provider}/{owner}/{name} must be indistinguishable from a repository that does not exist");
        }
    }

    /// <summary>
    /// The timing half, asserted as the work done rather than as elapsed time: a private name under
    /// a known owner must go all the way to the forge lookup, exactly like a made-up one.
    /// </summary>
    [Theory]
    [InlineData("secret")]
    [InlineData("old-secret")]
    [InlineData("made-up")]
    public async Task A_private_and_a_missing_name_under_a_known_owner_both_reach_the_forge_lookup(string name)
    {
        using var store = await SeedAsync(GetDocumentStore());

        foreach (var authenticated in new[] { false, true })
        {
            using var session = store.OpenAsyncSession();
            var (services, github) = CreateServices(session);

            await Controller<BrowseController>(services, authenticated).GetRepo("github", "acme", name, CancellationToken.None);

            github.AppClients.Should().Be(1,
                $"acme/{name} must resolve by the same steps as a name that does not exist (#453) — returning early on an invisible hit is the ~4 ms vs ~270 ms oracle");
        }
    }

    [Fact]
    public async Task A_visible_repository_still_resolves_from_the_database_alone()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var (services, github) = CreateServices(session);

        var result = await Controller<BrowseController>(services, authenticated: false)
            .GetRepo("github", "acme", "open", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>("anonymous visitors may browse a public repository");
        github.AppClients.Should().Be(0, "a live name the caller may see never needs GitHub");
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task The_badge_is_byte_identical_for_every_variant(string provider, string owner, string name)
    {
        using var store = await SeedAsync(GetDocumentStore());

        using var referenceSession = store.OpenAsyncSession();
        var (referenceServices, _) = CreateServices(referenceSession);
        var reference = Controller<BadgeController>(referenceServices, authenticated: false);
        var expected = ((ContentResult)await reference.Get("github", "acme", "made-up", null, null, null, null, CancellationToken.None)).Content;

        using var session = store.OpenAsyncSession();
        var (services, github) = CreateServices(session);
        var controller = Controller<BadgeController>(services, authenticated: false);
        var actual = ((ContentResult)await controller.Get(provider, owner, name, null, null, null, null, CancellationToken.None)).Content;

        actual.Should().Be(expected, "the badge must not render the private repository's 90%");
        controller.Response.Headers.ETag.ToString().Should().Be(reference.Response.Headers.ETag.ToString());
        controller.Response.Headers.CacheControl.ToString().Should().Be(reference.Response.Headers.CacheControl.ToString());
        if (provider == "github" && owner == "acme")
            github.AppClients.Should().Be(1, "a private name and a missing one must take the same path");
    }

    [Fact]
    public async Task The_badge_still_renders_a_private_repository_for_its_badge_token()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var (services, github) = CreateServices(session);

        var svg = ((ContentResult)await Controller<BadgeController>(services, authenticated: false)
            .Get("github", "acme", "secret", "the-real-badge-token", null, null, null, CancellationToken.None)).Content;

        svg.Should().Contain("90", "the token is the capability that makes a private badge visible");
        github.AppClients.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Repo_settings_refuse_every_variant_identically(string provider, string owner, string name)
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var (services, _) = CreateServices(session);

        var result = (await Controller<RepoSettingsController>(services, authenticated: true)
            .RotateBadgeToken(provider, owner, name, CancellationToken.None)).Result!;

        Describe(result).Should().Be("404", "a repository the caller cannot manage must look like one that does not exist");

        using var read = store.OpenAsyncSession();
        (await read.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, PrivateRepoId)))!
            .BadgeToken.Should().Be("the-real-badge-token", "nothing may be rotated by a non-member");
    }
}
