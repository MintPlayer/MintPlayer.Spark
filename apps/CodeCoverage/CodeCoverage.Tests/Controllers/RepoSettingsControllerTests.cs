using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;
using CodeCoverage.Forge;
using NSubstitute;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// Badge-token rotation and gate policy, at <b>0% coverage</b> before this.
/// <para>
/// The property most worth pinning is the one that looks like a bug: an unauthorized caller gets
/// <c>404</c>, not <c>403</c>. Answering 403 would confirm the repository exists, turning this
/// endpoint into an existence oracle for private repositories — the same disclosure the badge
/// endpoint already refuses to be. A future reader "fixing" the status code would quietly
/// reintroduce that, so the test says why.
/// </para>
/// </summary>
public class RepoSettingsControllerTests : CoverageRavenTest
{
    private const string Owner = "acme";
    private const string Name = "widget";
    private const long RepoId = 8080;

    private static RepoSettingsController CreateController(
        IAsyncDocumentSession session, TestGitHubAccessService access)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        services.AddSingleton<CodeCoverage.Services.IRepositoryResolver>(new TestRepositoryResolver(session));
        services.AddSingleton<CodeCoverage.Services.IGitHubAccessService>(access);
        // The real adapter and facade, so the authorization call this fixture counts still
        // happens where production would make it. The read/write halves are substituted
        // because settings never touch them.
        services.AddScoped<CodeCoverage.Services.IForgeAccessService, CodeCoverage.Services.GitHubForgeAccessService>();
        services.AddSingleton(Substitute.For<CodeCoverage.Services.IForgeClient>());
        services.AddSingleton(Substitute.For<CodeCoverage.Feedback.IForgeFeedbackPublisher>());
        services.AddScoped<IForgeIntegration, CodeCoverage.Services.GitHubForgeIntegration>();
        services.AddScoped<IForgeIntegrationResolver>(sp => new SingleForgeResolver(sp.GetRequiredService<IForgeIntegration>()));
        services.AddScoped<RepoSettingsController>();

        return services.BuildServiceProvider().GetRequiredService<RepoSettingsController>();
    }

    private static async Task SeedAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Repository
        {
            GitHubId = RepoId,
            Name = Name,
            FullName = $"{Owner}/{Name}",
            OwnerLogin = Owner,
        }, Repository.DocumentId(RepoId));
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// Not a 403. See the class remarks: a 403 here would confirm the repository exists to someone
    /// with no right to know that.
    /// </summary>
    [Fact]
    public async Task An_unauthorized_caller_gets_NotFound_rather_than_Forbidden()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new TestGitHubAccessService("someone-else"));

        Assert.IsType<NotFoundResult>((await controller.RotateBadgeToken(Owner, Name, default)).Result);
    }

    [Fact]
    public async Task Rotating_the_badge_token_replaces_it_and_persists()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);

        string? first, second;

        using (var session = store.OpenAsyncSession())
        {
            var controller = CreateController(session, new TestGitHubAccessService(Owner));
            Assert.IsType<OkObjectResult>((await controller.RotateBadgeToken(Owner, Name, default)).Result);

            using var read = store.OpenAsyncSession();
            first = (await read.LoadAsync<Repository>(Repository.DocumentId(RepoId)))!.BadgeToken;
        }

        Assert.False(string.IsNullOrWhiteSpace(first));

        using (var session = store.OpenAsyncSession())
        {
            var controller = CreateController(session, new TestGitHubAccessService(Owner));
            await controller.RotateBadgeToken(Owner, Name, default);

            using var read = store.OpenAsyncSession();
            second = (await read.LoadAsync<Repository>(Repository.DocumentId(RepoId)))!.BadgeToken;
        }

        // Rotation must actually rotate: the previous badge URL has to stop working, which is the
        // entire reason the endpoint exists.
        Assert.NotEqual(first, second);
    }
}
