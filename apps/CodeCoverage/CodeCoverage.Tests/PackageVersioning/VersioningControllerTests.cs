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

    // One key ring for the class, as one server: a plan sealed by one request unseals in the next (#552 D6).
    private static readonly Microsoft.AspNetCore.DataProtection.IDataProtectionProvider Sealer =
        new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();

    private sealed record Harness(VersioningController Controller, FakeGitWriter Writer, FakeForgeMergeQueue Queue, ScriptedDiffService Forge, FakeForgeVersioning Versioning);

    private static Harness Create(IDocumentStore store, ClaimsPrincipal user, string? yml = Versioned,
        FakeGitWriter? writer = null, VersioningRunLocks? locks = null)
    {
        var versioning = new FakeForgeVersioning();
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
        services.AddHttpClient();
        services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(Sealer);
        services.AddSingleton<IForgeVersioning>(versioning);
        services.AddSingleton<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>(new Dependencies.Lifecycle.DependencyOnboardingSurfaceTests.RecordingBus());
        services.AddScoped<IVersioningOnboardingService, VersioningOnboardingService>();
        services.AddScoped<VersioningController>();

        var controller = services.BuildServiceProvider().GetRequiredService<VersioningController>();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return new Harness(controller, writer, queue, forge, versioning);
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
    public async Task A_run_after_the_onboarding_merged_pushes_its_seed_tags_at_the_merge_commit_first()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var repositoryId = Repository.DocumentId(EForgeProvider.GitHub, RepoId);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new VersioningOnboardingState
            {
                PullRequestNumber = 9, Seeds = [new VersioningOnboardingSeed { PackageId = "Acme.Widget", Version = "1.1.0" }],
            }, VersioningOnboardingState.ForRepository(repositoryId));
            await seed.SaveChangesAsync();
        }
        var h = Create(store, Token());
        const string merge = "9999999999999999999999999999999999999999";
        h.Versioning.PullRequest = new ForgePullRequestText(9, VersioningOnboarding.Title, null, VersioningOnboarding.Branch, Sha, "main",
            IsOpen: false, Merged: true, AuthorIsBot: true, MergeCommitSha: merge, []);

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status200OK);

        var tags = h.Writer.TagRequests.Single();
        tags.Branches.Should().Equal("main");
        tags.Tags.Should().Equal(new GitWriterTag("Acme.Widget/v1.1.0", merge));
        h.Writer.VersioningRuns.Should().HaveCount(1);
        using var read = store.OpenAsyncSession();
        (await read.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repositoryId)))!.SeedCommit.Should().Be(merge);

        // Pushed once: the next run finds nothing pending.
        Status(await Create(store, Token(runId: "78")).Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task A_run_waits_when_the_merged_onboardings_seed_tags_are_refused()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var repositoryId = Repository.DocumentId(EForgeProvider.GitHub, RepoId);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new VersioningOnboardingState
            {
                PullRequestNumber = 9, Seeds = [new VersioningOnboardingSeed { PackageId = "Acme.Widget", Version = "1.1.0" }],
            }, VersioningOnboardingState.ForRepository(repositoryId));
            await seed.SaveChangesAsync();
        }
        var writer = new FakeGitWriter
        {
            Versioning = _ => Task.FromResult(Result(GitWriterVersioningResult.Versioned)),
            Tags = r => Task.FromResult(new GitWriterTagsResult(GitWriterTagsResult.Tagged, null, [new GitWriterTagResult(r.Tags[0].Name, "conflict", "points elsewhere")], null, 1)),
        };
        var h = Create(store, Token(), writer: writer);
        h.Versioning.PullRequest = new ForgePullRequestText(9, VersioningOnboarding.Title, null, VersioningOnboarding.Branch, Sha, "main",
            IsOpen: false, Merged: true, AuthorIsBot: true, MergeCommitSha: "9999999999999999999999999999999999999999", []);

        Status(await h.Controller.Run(Manifest(), default)).Should().Be(StatusCodes.Status503ServiceUnavailable);
        h.Writer.VersioningRuns.Should().BeEmpty();
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

    private const string Fingerprint = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private static FakeGitWriter PlanningWriter() => new()
    {
        Versioning = r => Task.FromResult(r.PlanOnly
            ? new GitWriterVersioningResult(GitWriterVersioningResult.Versioned, null,
                [new GitWriterRelease("Acme.Widget", "nuget", "1.2.0", "Acme.Widget/v1.2.0", ["api added"], null, false, "planned",
                    new GitWriterReleaseNotes("Acme.Widget 1.2.0", "notes", false))],
                [], [], [], null, 5, [], new TagPlan(Sha, Fingerprint, r.Run, [new PlannedTag("Acme.Widget/v1.2.0", null)]))
            : Result(GitWriterVersioningResult.Versioned)),
    };

    private static async Task<string> PlanAsync(IDocumentStore store, FakeGitWriter writer)
    {
        var result = await Create(store, Token(), writer: writer).Controller.Plan(Manifest(), default);
        Status(result).Should().Be(StatusCodes.Status200OK);
        var response = result.Should().BeAssignableTo<ObjectResult>().Which.Value.Should().BeOfType<VersioningController.VersioningRunResponse>().Which;
        response.Plan.Should().NotBeNull();
        return response.Seal.Should().NotBeNullOrEmpty().And.Subject!;
    }

    [Fact]
    public async Task A_plan_tags_nothing_and_its_release_tags_records_and_reports()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var writer = PlanningWriter();
        var seal = await PlanAsync(store, writer);

        writer.VersioningRuns.Single().PlanOnly.Should().BeTrue();
        writer.TagRequests.Should().BeEmpty();
        using (var read = store.OpenAsyncSession())
            (await read.LoadAsync<VersioningRun>(VersioningRun.DocumentId(EForgeProvider.GitHub, RepoId, 77, 2))).Should().BeNull("a plan records nothing");

        // Attempt 3 of the same run: "re-run failed jobs" releases the plan the first attempt made.
        var released = Create(store, new ClaimsPrincipal(new ClaimsIdentity(Token().Claims
            .Select(c => c.Type == Oidc.RunAttemptClaim ? new Claim(c.Type, "3") : c), Oidc.SchemeName)), writer: writer);
        var result = await released.Controller.Release(new VersioningController.VersioningReleaseRequest(seal), default);

        Status(result).Should().Be(StatusCodes.Status200OK);
        var tags = writer.TagRequests.Single();
        tags.Tags.Should().Equal(new GitWriterTag("Acme.Widget/v1.2.0", Sha, null));
        tags.Fingerprint.Should().Be(Fingerprint);
        tags.Run.Should().Be("https://github.com/acme/widget/actions/runs/77");
        tags.Branches.Should().Equal("main");
        var response = (VersioningController.VersioningRunResponse)((ObjectResult)result).Value!;
        response.Releases.Single().TagOutcome.Should().Be(GitWriterTagResult.Created);
        response.Releases.Single().Release.Should().NotBeNull();
        response.Seal.Should().BeNull();
        released.Forge.Statuses.Single().Verdict.Outcome.Should().Be(EForgeOutcome.Success);
        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<VersioningRun>(VersioningRun.DocumentId(EForgeProvider.GitHub, RepoId, 77, 3)))!.Releases.Single().Tag.Should().Be("Acme.Widget/v1.2.0");
    }

    [Theory]
    [InlineData("seal")]
    [InlineData("repository")]
    [InlineData("run")]
    [InlineData("sha")]
    [InlineData("branch")]
    public async Task A_release_is_refused_unless_its_token_matches_the_sealed_plan(string mismatch)
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var writer = PlanningWriter();
        var seal = await PlanAsync(store, writer);
        var token = mismatch switch
        {
            "repository" => Token(repositoryId: RepoId + 1),
            "run" => Token(runId: "78"),
            "sha" => Token(sha: "9999999999999999999999999999999999999999"),
            "branch" => Token("refs/heads/release/8.x"),
            _ => Token(),
        };

        var result = await Create(store, token, writer: writer).Controller.Release(
            new VersioningController.VersioningReleaseRequest(mismatch == "seal" ? seal[..^4] + "AAAA" : seal), default);

        Status(result).Should().Be(mismatch == "seal" ? StatusCodes.Status400BadRequest : StatusCodes.Status403Forbidden);
        writer.TagRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_release_is_a_409_so_the_action_does_not_fall_back()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var writer = PlanningWriter();
        writer.Tags = _ => Task.FromResult(new GitWriterTagsResult("aborted", VersioningController.StalePlan, null, "moved", 1));
        var seal = await PlanAsync(store, writer);

        Status(await Create(store, Token(), writer: writer).Controller.Release(new VersioningController.VersioningReleaseRequest(seal), default))
            .Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task A_released_plan_released_again_answers_the_same()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var writer = PlanningWriter();
        var seal = await PlanAsync(store, writer);
        Status(await Create(store, Token(), writer: writer).Controller.Release(new VersioningController.VersioningReleaseRequest(seal), default))
            .Should().Be(StatusCodes.Status200OK);
        writer.Tags = r => Task.FromResult(new GitWriterTagsResult(GitWriterTagsResult.Tagged, null,
            [.. r.Tags.Select(t => new GitWriterTagResult(t.Name, GitWriterTagResult.AlreadyPresent, null))], null, 1));

        var again = await Create(store, Token(), writer: writer).Controller.Release(new VersioningController.VersioningReleaseRequest(seal), default);

        Status(again).Should().Be(StatusCodes.Status200OK);
        ((VersioningController.VersioningRunResponse)((ObjectResult)again).Value!).Releases.Single().Release.Should().NotBeNull(
            "a re-run that finds its tags still creates the Releases it missed");
    }
}
