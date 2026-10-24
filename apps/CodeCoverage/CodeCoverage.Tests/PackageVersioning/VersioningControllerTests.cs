using System.Security.Claims;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests.MergeQueue;
using CodeCoverage.Tests.Services;
using CodeCoverage.Versioning.Releases;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// <c>POST /api/versioning/run</c> (#153 M6): which tokens may tag what (S6), how the writer's outcomes map onto the
/// statuses the action falls back on (D2), and that runs of one repository never overlap.
/// </summary>
public class VersioningControllerTests : CoverageRavenTest
{
    private const long RepoId = 1530;
    private const string Sha = "1530153015301530153015301530153015301530";
    private const string Versioned = "versioning:\n  releaseBranches: ['release/*.x']\n";

    private static readonly ForgeOidcProfile Oidc = ForgeOidcProfile.GitHub;

    private sealed record Harness(VersioningController Controller, FakeGitWriter Writer, FakeForgeMergeQueue Queue, ScriptedDiffService Forge);

    private static Harness Create(IDocumentStore store, ClaimsPrincipal user, string? yml = Versioned,
        FakeGitWriter? writer = null, VersioningRunLocks? locks = null)
    {
        var forge = new ScriptedDiffService();
        if (yml is not null) forge.Files[$"main/{CoverageYml.FileName}"] = yml;
        var queue = new FakeForgeMergeQueue();
        writer ??= new FakeGitWriter { Versioning = _ => Task.FromResult(Result(GitWriterVersioningResult.Versioned)) };

        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(store.OpenAsyncSession());
        services.AddSingleton<IForgeIntegration>(forge);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton<IForgeMergeQueue>(queue);
        services.AddSingleton<IGitWriterSidecar>(writer);
        services.AddSingleton(locks ?? new VersioningRunLocks());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<VersioningController>();

        var controller = services.BuildServiceProvider().GetRequiredService<VersioningController>();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return new Harness(controller, writer, queue, forge);
    }

    private static ClaimsPrincipal Token(string gitRef = "refs/heads/main", string sha = Sha, long repositoryId = RepoId, string runId = "77")
        => new(new ClaimsIdentity(
        [
            new Claim(Oidc.RepositoryClaim, "acme/widget"),
            new Claim(Oidc.RepositoryIdClaim, repositoryId.ToString()),
            new Claim(Oidc.RunIdClaim, runId),
            new Claim(Oidc.RunAttemptClaim!, "2"),
            new Claim(Oidc.RefClaim, gitRef),
            new Claim(Oidc.CommitClaim, sha),
            new Claim(Oidc.WorkflowRefClaim, "acme/widget/.github/workflows/publish.yml@refs/heads/main"),
        ], Oidc.SchemeName));

    private static ReleaseManifest Manifest(string commit = Sha) => new()
    {
        Commit = commit,
        Packages = [new CodeCoverage.Versioning.Releases.ManifestPackage { Id = "Acme.Widget", Kind = "nuget", Packed = true }],
    };

    private static GitWriterVersioningResult Result(string outcome, string? reason = null) => new(outcome, reason,
        [new GitWriterRelease("Acme.Widget", "nuget", "1.2.0", "Acme.Widget/1.2.0", ["api added"], null, false, "created")],
        [], [], [], null, 5);

    private static async Task SeedRepositoryAsync(IDocumentStore store)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Repository { GitHubId = RepoId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", DefaultBranch = "main" },
            Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        await seed.SaveChangesAsync();
    }

    private static int? Status(IActionResult result) => result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode;

    [Fact]
    public async Task A_run_on_the_default_branch_tags_records_and_reports()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store, Token());

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status200OK);

        var request = h.Writer.VersioningRuns.Single();
        request.Branch.Should().Be("main");
        request.DefaultBranch.Should().Be("main");
        request.BranchMajor.HasValue.Should().BeFalse();
        request.Manifest.Commit.Should().Be(Sha);
        h.Queue.RevokedTokens.Should().HaveCount(1);
        var check = h.Forge.Statuses.Single();
        check.Name.Should().Be(VersioningController.CheckName);
        check.Verdict.Outcome.Should().Be(EForgeOutcome.Success);

        using var read = store.OpenAsyncSession();
        var run = await read.LoadAsync<VersioningRun>(VersioningRun.DocumentId(EForgeProvider.GitHub, RepoId, 77, 2));
        run.Should().NotBeNull();
        run!.Outcome.Should().Be(GitWriterVersioningResult.Versioned);
        run.WorkflowRef.Should().Be("acme/widget/.github/workflows/publish.yml@refs/heads/main");
        run.Releases.Single().Tag.Should().Be("Acme.Widget/1.2.0");
    }

    [Fact]
    public async Task A_release_branch_run_carries_the_major_its_name_holds()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store, Token("refs/heads/release/8.x"));

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status200OK);

        h.Writer.VersioningRuns.Single().BranchMajor.Should().Be(8);
    }

    [Theory]
    [InlineData("refs/pull/12/merge")]
    [InlineData("refs/tags/v1.0.0")]
    [InlineData("refs/heads/feature/x")]
    public async Task Only_the_default_branch_and_release_branches_may_tag(string gitRef)
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store, Token(gitRef));

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status403Forbidden);
        h.Writer.VersioningRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_repository_is_refused()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store, Token(repositoryId: 999));

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status403Forbidden);
        h.Writer.VersioningRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task The_manifest_cannot_name_a_commit_other_than_the_tokens()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store, Token());

        Status(await h.Controller.Run(Manifest(new string('a', 40)), default)).Should().Be(StatusCodes.Status403Forbidden);
        h.Writer.VersioningRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task A_malformed_manifest_is_a_400()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store, Token());

        var result = await h.Controller.Run(Manifest() with { Commit = "not-a-sha" }, default);

        Status(result).Should().Be(StatusCodes.Status400BadRequest);
        ((ObjectResult)result).Value.Should().BeOfType<VersioningController.VersioningRunResponse>().Which.Errors!.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_repository_without_a_versioning_section_is_a_409()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store, Token(), yml: "comment:\n  enabled: true\n");

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task An_unusable_versioning_section_is_a_422()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store, Token(), yml: "versioning:\n  mode: sometimes\n");

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task A_refused_plan_is_a_422_so_the_action_does_not_fall_back()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var writer = new FakeGitWriter { Versioning = _ => Task.FromResult(Result(GitWriterVersioningResult.PlanRefused)) };
        var h = Create(store, Token(), writer: writer);

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status422UnprocessableEntity);
        h.Forge.Statuses.Single().Verdict.Outcome.Should().Be(EForgeOutcome.Failure);
    }

    [Fact]
    public async Task An_unreachable_writer_is_a_502_and_the_access_is_still_revoked()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var writer = new FakeGitWriter { Versioning = _ => throw new HttpRequestException("connection refused") };
        var h = Create(store, Token(), writer: writer);

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status502BadGateway);
        h.Queue.RevokedTokens.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_unconfigured_writer_is_a_503()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var h = Create(store, Token(), writer: new FakeGitWriter { IsConfigured = false });

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Concurrent_runs_of_one_repository_are_serialized()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var locks = new VersioningRunLocks();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = 0;
        var overlapped = false;
        var writer = new FakeGitWriter
        {
            Versioning = async _ =>
            {
                if (Interlocked.Increment(ref inside) > 1) overlapped = true;
                entered.TrySetResult();
                await release.Task;
                Interlocked.Decrement(ref inside);
                return Result(GitWriterVersioningResult.Versioned);
            },
        };
        var first = Create(store, Token(runId: "1"), writer: writer, locks: locks).Controller.Run(Manifest(), default);
        var second = Create(store, Token(runId: "2"), writer: writer, locks: locks).Controller.Run(Manifest(), default);

        // The first holds the writer; the second, given time to reach it, must still be waiting at the lock.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        writer.VersioningRuns.Should().HaveCount(1);
        release.SetResult();
        await Task.WhenAll(first, second);

        writer.VersioningRuns.Should().HaveCount(2);
        overlapped.Should().BeFalse();
    }
}
