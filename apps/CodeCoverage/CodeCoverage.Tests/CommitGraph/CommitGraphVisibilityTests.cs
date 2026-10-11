using System.Security.Claims;
using System.Text.Json;
using CodeCoverage.CommitGraph;
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
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.CommitGraph;

/// <summary>
/// PRD §5.1 for the graph endpoint: a hidden private repository, a missing one and an unknown forge
/// answer identically — 401 anonymous, 404 signed-in, one body — <b>including while the hidden
/// repository's mirror is syncing</b>, and none of them touches the mirror or its state.
/// </summary>
/// <remarks>
/// Run against the real <see cref="RepositoryResolver"/>, as <c>RepositoryExistenceParityTests</c>
/// is, because the oracle this closes would live in the path, not in the refusal's shape.
/// </remarks>
public class CommitGraphVisibilityTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long PrivateRepoId = 5040;
    private const long PublicRepoId = 5041;
    private static readonly DateTime SyncingSince = new(2026, 10, 11, 11, 59, 0, DateTimeKind.Utc);

    public static TheoryData<string, string, string> Variants => new()
    {
        { "github", "acme", "secret" },          // private, mirror syncing
        { "github", "acme", "made-up" },         // missing, under a known owner
        { "github", "stranger", "anything" },    // unknown owner
        { "nosuchforge", "acme", "secret" },     // unknown forge
    };

    private async Task SeedAsync(IDocumentStore store)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(
                new Account { GitHubId = 5, Provider = EForgeProvider.GitHub, Login = "acme", Type = "Organization" },
                Account.DocumentId(EForgeProvider.GitHub, 5));
            await seed.StoreAsync(new Repository
            {
                GitHubId = PrivateRepoId, Provider = EForgeProvider.GitHub,
                Name = "secret", FullName = "acme/secret", OwnerLogin = "acme", IsPrivate = true,
            }, Repository.DocumentId(EForgeProvider.GitHub, PrivateRepoId));
            await seed.StoreAsync(new Repository
            {
                GitHubId = PublicRepoId, Provider = EForgeProvider.GitHub,
                Name = "open", FullName = "acme/open", OwnerLogin = "acme", IsPrivate = false,
            }, Repository.DocumentId(EForgeProvider.GitHub, PublicRepoId));

            // The hidden repository's mirror is being cloned right now.
            await seed.StoreAsync(new RepositoryGraphState
            {
                Repository = Repository.DocumentId(EForgeProvider.GitHub, PrivateRepoId),
                Syncing = true, SyncingSinceUtc = SyncingSince, LastViewedAtUtc = SyncingSince,
            }, RepositoryGraphState.ForRepository(Repository.DocumentId(EForgeProvider.GitHub, PrivateRepoId)));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);
    }

    private static BrowseController Controller(IAsyncDocumentSession session, bool authenticated)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddMemoryCache();
        services.AddSingleton(session);
        services.AddSingleton<IGitHubInstallationService>(new StubGitHub { Throws = new InvalidOperationException("no GitHub in tests") });
        services.AddSingleton<IGitHubIndexQueries, GitHubIndexQueries>();
        services.AddScoped<IRepositoryResolver, RepositoryResolver>();
        // A signed-in caller who administers nothing: the "authenticated non-member".
        var forge = ScriptedForge.From(new([], GitHubTokenState.Ok));
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton<IGitHubAccessService>(new ScriptedAccessService(new([], GitHubTokenState.Ok)));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<BrowseController>();
        var provider = services.BuildServiceProvider();

        var controller = provider.GetRequiredService<BrowseController>();
        var user = authenticated
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "outsider")], "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user, RequestServices = provider },
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
    public async Task The_graph_refuses_every_variant_with_the_canonical_refusal(string provider, string owner, string name)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        var kit = new CommitGraphKit(store);

        foreach (var authenticated in new[] { false, true })
        {
            using var session = store.OpenAsyncSession();
            var result = (await Controller(session, authenticated).GetGraph(
                provider, owner, name, kit.Get<ICommitGraphReader>(), cancellationToken: CancellationToken.None)).Result!;

            Describe(result).Should().Be(
                authenticated ? """404 {"error":"Not found"}""" : """401 {"error":"Authentication required"}""",
                $"{provider}/{owner}/{name} must be indistinguishable from a repository that does not exist, syncing or not");
        }

        // Nothing past the gate ran: no sidecar call, nothing queued, the hidden state untouched.
        kit.Sidecar.Requests.Should().BeEmpty();
        kit.Bus.Messages.Should().BeEmpty();
        using var check = store.OpenAsyncSession();
        var state = await check.LoadAsync<RepositoryGraphState>(
            RepositoryGraphState.ForRepository(Repository.DocumentId(EForgeProvider.GitHub, PrivateRepoId)));
        state!.LastViewedAtUtc.Should().Be(SyncingSince, "a refused read must not even stamp the view");
        (await check.LoadAsync<RepositoryGraphState>(
            RepositoryGraphState.ForRepository(Repository.DocumentId(EForgeProvider.GitHub, 999)))).Should().BeNull();
    }

    [Fact]
    public async Task A_visible_repository_without_a_mirror_answers_syncing_and_asks_for_the_clone()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        var kit = new CommitGraphKit(store);

        using var session = store.OpenAsyncSession();
        var result = (await Controller(session, authenticated: false).GetGraph(
            "github", "acme", "open", kit.Get<ICommitGraphReader>(), cancellationToken: CancellationToken.None)).Result;

        var ok = result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().BeOfType<CommitGraphResponse>().Which.State.Should().Be("syncing");
        kit.Bus.Of<FetchCommitGraphMessage>().Should().ContainSingle().Which.AllowClone.Should().BeTrue();
    }
}
