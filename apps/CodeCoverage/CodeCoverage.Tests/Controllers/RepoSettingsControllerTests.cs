using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

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

        Assert.IsType<NotFoundResult>((await controller.GetGate(Owner, Name, default)).Result);
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

    /// <summary>
    /// An unset gate reads back as the defaults rather than null, so the UI never has to guess
    /// them — and so "informational, auto-ratchet" is stated in one place.
    /// </summary>
    [Fact]
    public async Task An_unset_gate_reads_back_as_explicit_defaults()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new TestGitHubAccessService(Owner));

        var gate = Assert.IsType<GateSettings>(
            Assert.IsType<OkObjectResult>((await controller.GetGate(Owner, Name, default)).Result).Value);

        Assert.NotNull(gate);
    }

    [Theory]
    // projectMode
    [InlineData("nonsense", "scoped", null, null, 0d, 0d)]
    // projectBasis
    [InlineData("auto", "nonsense", null, null, 0d, 0d)]
    // percentages out of range
    [InlineData("auto", "scoped", 101.0, null, 0d, 0d)]
    [InlineData("auto", "scoped", null, 101.0, 0d, 0d)]
    [InlineData("auto", "scoped", null, null, 101.0, 0d)]
    [InlineData("auto", "scoped", null, null, 0d, 101.0)]
    // fixed mode with no target is contradictory
    [InlineData("fixed", "scoped", null, null, 0d, 0d)]
    public async Task An_invalid_gate_is_rejected_and_nothing_is_persisted(
        string projectMode, string projectBasis,
        double? projectTarget, double? patchTarget, double projectThreshold, double patchThreshold)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new TestGitHubAccessService(Owner));

        var gate = new GateSettings
        {
            ProjectMode = projectMode,
            ProjectBasis = projectBasis,
            ProjectTarget = projectTarget,
            PatchTarget = patchTarget,
            ProjectThreshold = projectThreshold,
            PatchThreshold = patchThreshold,
        };

        var result = await controller.PutGate(Owner, Name, gate, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);

        using var verify = store.OpenAsyncSession();
        Assert.Null((await verify.LoadAsync<Repository>(Repository.DocumentId(RepoId)))!.Gate);
    }

    /// <summary>
    /// Validation runs BEFORE the repository is resolved, so an invalid body is a 400 even for a
    /// repository the caller may not see. That ordering is deliberate and worth pinning: it means
    /// the endpoint cannot be used to probe existence by sending deliberately bad input.
    /// </summary>
    [Fact]
    public async Task An_invalid_gate_is_a_BadRequest_even_when_the_caller_is_unauthorized()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new TestGitHubAccessService("someone-else"));

        var result = await controller.PutGate(Owner, Name,
            new GateSettings { ProjectMode = "nonsense", ProjectBasis = "scoped" }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task A_valid_gate_is_stored()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
        {
            var controller = CreateController(session, new TestGitHubAccessService(Owner));
            var result = await controller.PutGate(Owner, Name,
                new GateSettings { ProjectMode = "fixed", ProjectBasis = "projection", ProjectTarget = 80 }, default);

            Assert.IsType<OkObjectResult>(result.Result);
        }

        using var verify = store.OpenAsyncSession();
        var stored = (await verify.LoadAsync<Repository>(Repository.DocumentId(RepoId)))!.Gate;

        Assert.NotNull(stored);
        Assert.Equal("fixed", stored!.ProjectMode);
        Assert.Equal(80, stored.ProjectTarget);
    }
}
