using System.Security.Claims;
using System.Text.Json;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests.MergeQueue;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The commit graph's release overlay (#544 D5): everyone who may browse sees a release run's status; only a viewer whose
/// verified forge login has write access may re-run its failed jobs, never while a newer run of the workflow is unfinished
/// (its <c>cancel-in-progress</c> would cancel that one). Anonymous callers and CSRF are the pipeline's (the route snapshot).
/// </summary>
public class ReleaseRunsControllerTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long RepoGitHubId = 910044;
    private static readonly string RepositoryId = Repository.DocumentId(EForgeProvider.GitHub, RepoGitHubId);

    private static readonly ForgeWorkflowRun Failed = new(41, "completed", "failure", "aaaa", "master", "https://github.com/acme/widget/actions/runs/41", 7);
    private static readonly ForgeWorkflowRun Cancelled = new(42, "completed", "cancelled", "bbbb", "master", "https://github.com/acme/widget/actions/runs/42", 7);
    private static readonly ForgeWorkflowRun Succeeded = new(43, "completed", "success", "cccc", "master", "https://github.com/acme/widget/actions/runs/43", 7);
    private static readonly ForgeWorkflowRun Running = new(44, "in_progress", null, "dddd", "master", "https://github.com/acme/widget/actions/runs/44", 7);

    private sealed class World
    {
        public GitHubVerifiedLogin? Verified { get; set; }
        public bool QueueSupported { get; set; } = true;
        public FakeForgeMergeQueue Queue { get; } = new();

        public World()
        {
            foreach (var run in new[] { Failed, Cancelled, Succeeded, Running }) Queue.Runs[run.Id] = run;
        }
    }

    private static ReleaseRunsController Controller(IAsyncDocumentSession session, World world, ClaimsPrincipal? user = null)
    {
        var visibility = new GitHubVisibility([], GitHubTokenState.Ok);
        var forge = ScriptedForge.From(visibility);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddMemoryCache();
        services.AddSingleton(session);
        services.AddSingleton<IRepositoryResolver>(new TestRepositoryResolver(session));
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton<IGitHubAccessService>(new ScriptedAccessService(visibility, world.Verified));
        // The real adapter: the role is read for the login IGitHubAccessService verified, as the merge-queue panel reads it.
        services.AddScoped<IForgeAccessService, GitHubForgeAccessService>();
        if (world.QueueSupported) services.AddSingleton<IForgeMergeQueue>(world.Queue);
        services.AddScoped<IRepositoryScope, RepositoryScope>();
        services.AddScoped<ReleaseRunsController>();
        var provider = services.BuildServiceProvider();

        var controller = provider.GetRequiredService<ReleaseRunsController>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()), RequestServices = provider },
        };
        return controller;
    }

    private static ClaimsPrincipal SignedIn()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "SparkUsers/1"), new Claim(ClaimTypes.Name, "acme")], "Identity.Application"));

    private static World Maintainer(EForgeRepositoryRole role = EForgeRepositoryRole.Write)
    {
        var world = new World { Verified = new GitHubVerifiedLogin("maintainer", true, GitHubTokenState.Ok) };
        world.Queue.Roles["maintainer"] = role;
        return world;
    }

    private async Task<IDocumentStore> SeedAsync(IDocumentStore store)
    {
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Repository
            {
                GitHubId = RepoGitHubId, Provider = EForgeProvider.GitHub, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme",
                DefaultBranch = "master",
            }, RepositoryId);
            await session.SaveChangesAsync();
        }
        WaitForIndexing(store);
        return store;
    }

    private static IReadOnlyList<ReleaseRunsController.ReleaseRunView> Views(ActionResult<IReadOnlyList<ReleaseRunsController.ReleaseRunView>> result)
        => (IReadOnlyList<ReleaseRunsController.ReleaseRunView>)((OkObjectResult)result.Result!).Value!;

    private static string Describe(IActionResult result) => result switch
    {
        JsonResult json => $"{json.StatusCode} {JsonSerializer.Serialize(json.Value, JsonSerializerOptions.Web)}",
        ObjectResult obj => $"{obj.StatusCode} {JsonSerializer.Serialize(obj.Value, JsonSerializerOptions.Web)}",
        StatusCodeResult status => $"{status.StatusCode}",
        var other => other.GetType().Name,
    };

    // ── The status ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_anonymous_visitor_sees_each_runs_status_and_no_button()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = new World();

        var views = Views(await Controller(session, world).Get("github", "acme", "widget", "41, 43,44,404,x,-1,41", default));

        views.Select(v => (v.Id, v.Status, v.Conclusion, v.CanRerun)).Should().Equal(
            [(41L, "completed", "failure", false), (43L, "completed", "success", false), (44L, "in_progress", null, false)],
            "an unknown, malformed or repeated id is left out");
        views[0].HtmlUrl.Should().Be(Failed.HtmlUrl);
        world.Queue.RoleQueries.Should().BeEmpty("nobody to ask about");
    }

    [Fact]
    public async Task A_writer_may_rerun_a_failed_or_cancelled_run_unless_a_newer_run_is_unfinished()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = Maintainer();
        world.Queue.NewerUnfinished.Add(Cancelled.Id);

        var views = Views(await Controller(session, world, SignedIn()).Get("github", "acme", "widget", "41,42,43,44", default));

        views.Select(v => (v.Id, v.CanRerun, v.Blocked is not null)).Should().Equal(
            [(41L, true, false), (42L, false, true), (43L, false, false), (44L, false, false)]);
        world.Queue.RoleQueries.Should().Equal(["maintainer"], "the verified login, once per request");
    }

    [Fact]
    public async Task A_triager_sees_the_status_but_no_button()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();

        var views = Views(await Controller(session, Maintainer(EForgeRepositoryRole.Triage), SignedIn()).Get("github", "acme", "widget", "41", default));

        views.Should().ContainSingle().Which.CanRerun.Should().BeFalse();
    }

    [Fact]
    public async Task More_runs_than_a_page_holds_are_cut_at_the_cap()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = new World();
        for (var id = 100L; id < 130; id++) world.Queue.Runs[id] = Succeeded with { Id = id };

        var views = Views(await Controller(session, world).Get("github", "acme", "widget", string.Join(',', Enumerable.Range(100, 30)), default));

        views.Should().HaveCount(ReleaseRunsController.MaxRuns);
    }

    // ── The button ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_writer_reruns_the_failed_jobs()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = Maintainer(EForgeRepositoryRole.Maintain);

        Describe(await Controller(session, world, SignedIn()).Rerun("github", "acme", "widget", Cancelled.Id, default)).Should().StartWith("202");

        world.Queue.RerunFailedJobs.Should().Equal([Cancelled.Id]);
    }

    [Theory]
    [InlineData(false, EForgeRepositoryRole.Write, "403 ")]
    [InlineData(true, EForgeRepositoryRole.Triage, "403 ")]
    [InlineData(true, EForgeRepositoryRole.None, "403 ")]
    public async Task Only_a_signed_in_writer_may_rerun(bool signedIn, EForgeRepositoryRole role, string expected)
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = Maintainer(role);

        Describe(await Controller(session, world, signedIn ? SignedIn() : null).Rerun("github", "acme", "widget", Failed.Id, default))
            .Should().StartWith(expected);
        world.Queue.RerunFailedJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_that_needs_no_rerun_or_would_cancel_a_newer_one_is_refused()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = Maintainer();
        world.Queue.NewerUnfinished.Add(Failed.Id);

        Describe(await Controller(session, world, SignedIn()).Rerun("github", "acme", "widget", Succeeded.Id, default)).Should().StartWith("409 ");
        Describe(await Controller(session, world, SignedIn()).Rerun("github", "acme", "widget", Running.Id, default)).Should().StartWith("409 ");
        Describe(await Controller(session, world, SignedIn()).Rerun("github", "acme", "widget", Failed.Id, default))
            .Should().StartWith("409 ").And.Contain("newer run");
        Describe(await Controller(session, world, SignedIn()).Rerun("github", "acme", "widget", 404, default)).Should().StartWith("404 ");
        world.Queue.RerunFailedJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_forge_refusing_says_what_to_do()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = Maintainer();

        world.Queue.RerunFailedJobsResult = false;
        Describe(await Controller(session, world, SignedIn()).Rerun("github", "acme", "widget", Failed.Id, default)).Should().StartWith("422 ");

        world.Queue.RerunFailedJobsThrows = new ForgeAccessDeniedException("Resource not accessible by integration", "actions=write");
        Describe(await Controller(session, world, SignedIn()).Rerun("github", "acme", "widget", Failed.Id, default))
            .Should().StartWith("403 ").And.Contain("Actions (write)");
    }

    [Fact]
    public async Task A_forge_without_runs_has_no_overlay_and_no_button()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();
        var world = Maintainer();
        var noQueue = new World { QueueSupported = false, Verified = world.Verified };

        Views(await Controller(session, noQueue, SignedIn()).Get("github", "acme", "widget", "41", default)).Should().BeEmpty();
        Describe(await Controller(session, noQueue, SignedIn()).Rerun("github", "acme", "widget", 41, default)).Should().StartWith("409 ");
    }

    [Fact]
    public async Task An_unknown_repository_is_refused_as_the_browse_api_refuses()
    {
        using var store = await SeedAsync(GetDocumentStore());
        using var session = store.OpenAsyncSession();

        var get = (await Controller(session, new World()).Get("github", "acme", "nothing", "41", default)).Result;
        Describe(get!).Should().StartWith("401 ");
        Describe(await Controller(session, Maintainer(), SignedIn()).Rerun("github", "acme", "nothing", 41, default)).Should().StartWith("404 ");
    }
}
