using System.Text;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The anonymous fork-upload endpoint, arm by arm.
/// </summary>
/// <remarks>
/// It is a live anonymous WRITE endpoint — <c>action/src/main.ts</c> posts to it for every fork
/// pull request — and until this file nothing had executed it. Every gate is asserted, and so is
/// the one property that holds them together: each refusal is the same 404 with the same body, so
/// an anonymous caller cannot tell a private repository from a missing one, or an installed
/// repository from one that is not.
/// </remarks>
public class ForkUploadsControllerTests
{
    private const string HeadSha = "f0f0f0f0a1a1a1a1b2b2b2b2c3c3c3c3d4d4d4d4";
    private const long BaseRepositoryId = 9001;

    private sealed class FixedResolver(Repository? repository, bool honourVisibility = true) : IRepositoryResolver
    {
        public Task<RepositoryResolution> ResolveAsync(
            EForgeProvider provider, string owner, string name, Func<Repository, bool> isVisible,
            CancellationToken cancellationToken = default)
            => Task.FromResult(repository is null || (honourVisibility && !isVisible(repository))
                ? RepositoryResolution.None
                : new RepositoryResolution(repository, false));
    }

    private static Repository PublicRepository() => new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, BaseRepositoryId),
        GitHubId = BaseRepositoryId,
        Name = "widget",
        FullName = "acme/widget",
        OwnerLogin = "acme",
        IsPrivate = false,
    };

    private static ForgePullRequest ForkPull(string headSha = HeadSha, long? headRepositoryId = 777, string? defaultBranch = "main")
        => new(42, headSha, "feature/x", headRepositoryId, "main", BaseRepositoryId, defaultBranch, IsOpen: true);

    private sealed record Harness(
        ForkUploadsController Controller, IForgeIntegration Forge, IUploadIngestor Ingestor, List<UploadIngestRequest> Ingested);

    private static Harness Create(Repository? repository, ForgePullRequest? pull = null, bool honourVisibility = true)
    {
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        forge.GetPullRequestAsync(Arg.Any<Repository>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(pull));

        var ingested = new List<UploadIngestRequest>();
        var ingestor = Substitute.For<IUploadIngestor>();
        ingestor.IngestAsync(Arg.Any<UploadIngestRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                ingested.Add(call.Arg<UploadIngestRequest>());
                return Task.FromResult(new UploadIngestResult("Builds/1", "session-1"));
            });

        var controller = new ForkUploadsController(
            new FixedResolver(repository, honourVisibility),
            new SingleForgeResolver(forge),
            ingestor,
            NullLogger<ForkUploadsController>.Instance);
        return new Harness(controller, forge, ingestor, ingested);
    }

    private static IFormFile Report(string name = "lcov.info") =>
        new FormFile(new MemoryStream(Encoding.UTF8.GetBytes("TN:\nSF:a.ts\nDA:1,1\nend_of_record\n")), 0, 34, name, name);

    private static ForkUploadsController.ForkUploadForm Form(int files = 1, string sha = HeadSha, string? fileList = null)
        => new()
        {
            CommitSha = sha,
            RunId = 12345,
            RunAttempt = 2,
            Workflow = "ci",
            JobName = "test",
            Flags = "unit",
            FileList = fileList,
            Files = [.. Enumerable.Range(0, files).Select(i => Report($"r{i}.info"))],
        };

    private static Task<IActionResult> Upload(Harness h, ForkUploadsController.ForkUploadForm form, string provider = "github")
        => h.Controller.UploadFromFork(provider, "acme", "widget", 42, form, CancellationToken.None);

    /// <summary>The single refusal: one status, one body.</summary>
    private static void ShouldBeTheUniformRefusal(IActionResult result)
    {
        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Which;
        notFound.Value!.ToString().Should().Contain("No such pull request, or coverage cannot be accepted for it.");
    }

    [Fact]
    public async Task A_fork_pull_request_whose_head_matches_is_accepted_with_forge_supplied_identity()
    {
        var repository = PublicRepository();
        var h = Create(repository, ForkPull());

        var result = await Upload(h, Form());

        var accepted = result.Should().BeOfType<AcceptedResult>().Which;
        accepted.Value!.ToString().Should().Contain("Builds/1");
        var request = h.Ingested.Should().ContainSingle().Which;
        request.CommitSha.Should().Be(HeadSha);
        request.Branch.Should().Be("feature/x");
        request.PullRequestNumber.Should().Be(42);
        request.BaseRef.Should().Be("main");
        request.EventName.Should().Be("pull_request");
        request.ContributedFromFork.Should().BeTrue();
        request.CarryForward.Should().BeFalse();
        request.RunId.Should().Be(12345);
        request.RunAttempt.Should().Be(2);
        request.Flags.Should().Be("unit");

        // Learned while the forge's answer was at hand, and the budget spent only on success.
        repository.DefaultBranch.Should().Be("main");
        repository.ForkUploads!.Count.Should().Be(1);
    }

    /// <summary>A repository that already knows its default branch keeps it; the pull request does not overwrite it.</summary>
    [Fact]
    public async Task A_known_default_branch_is_not_overwritten()
    {
        var repository = PublicRepository();
        repository.DefaultBranch = "trunk";
        var h = Create(repository, ForkPull(defaultBranch: "main"));

        (await Upload(h, Form())).Should().BeOfType<AcceptedResult>();

        repository.DefaultBranch.Should().Be("trunk");
    }

    /// <summary>A deleted head repository reports a null head id, which counts as a fork by design.</summary>
    [Fact]
    public async Task A_pull_request_whose_head_repository_was_deleted_counts_as_a_fork()
    {
        var h = Create(PublicRepository(), ForkPull(headRepositoryId: null));

        (await Upload(h, Form())).Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task An_upload_with_no_files_is_refused_uniformly()
        => ShouldBeTheUniformRefusal(await Upload(Create(PublicRepository(), ForkPull()), Form(files: 0)));

    [Fact]
    public async Task Too_many_files_is_a_bad_request_before_anything_is_resolved()
    {
        var h = Create(PublicRepository(), ForkPull());

        var result = await Upload(h, Form(files: 65));

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("Too many report files");
        await h.Forge.DidNotReceiveWithAnyArgs().GetPullRequestAsync(default!, default, default);
    }

    [Fact]
    public async Task At_the_file_limit_the_upload_is_not_refused_by_the_bound()
        => (await Upload(Create(PublicRepository(), ForkPull()), Form(files: 64))).Should().BeOfType<AcceptedResult>();

    [Fact]
    public async Task An_oversized_file_list_is_a_bad_request()
    {
        var result = await Upload(Create(PublicRepository(), ForkPull()), Form(fileList: new string('a', 1024 * 1024 + 1)));

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("file list is too large");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123")]
    public async Task A_missing_or_short_sha_is_refused_uniformly(string sha)
        => ShouldBeTheUniformRefusal(await Upload(Create(PublicRepository(), ForkPull()), Form(sha: sha)));

    [Fact]
    public async Task An_unknown_forge_is_refused_uniformly()
        => ShouldBeTheUniformRefusal(await Upload(Create(PublicRepository(), ForkPull()), Form(), provider: "nosuchforge"));

    [Fact]
    public async Task An_unknown_repository_is_refused_uniformly()
        => ShouldBeTheUniformRefusal(await Upload(Create(repository: null, ForkPull()), Form()));

    [Fact]
    public async Task A_disconnected_repository_is_refused_uniformly()
    {
        var repository = PublicRepository();
        repository.Connection = RepositoryConnection.Disconnected;

        ShouldBeTheUniformRefusal(await Upload(Create(repository, ForkPull()), Form()));
    }

    /// <summary>The resolver's visibility already hides a private repository (#453).</summary>
    [Fact]
    public async Task A_private_repository_is_invisible_to_the_resolver_and_refused_uniformly()
    {
        var repository = PublicRepository();
        repository.IsPrivate = true;

        ShouldBeTheUniformRefusal(await Upload(Create(repository, ForkPull()), Form()));
    }

    /// <summary>
    /// And the controller's own public-only check still holds if a resolver ever stopped honouring
    /// the visibility it was handed — the explicit statement of the rule, not just a side effect.
    /// </summary>
    [Fact]
    public async Task A_private_repository_is_refused_even_if_the_resolver_returns_it()
    {
        var repository = PublicRepository();
        repository.IsPrivate = true;
        var h = Create(repository, ForkPull(), honourVisibility: false);

        ShouldBeTheUniformRefusal(await Upload(h, Form()));
        await h.Forge.DidNotReceiveWithAnyArgs().GetPullRequestAsync(default!, default, default);
    }

    /// <summary>Refused before the forge round trip, and the refusal spends nothing from the budget.</summary>
    [Fact]
    public async Task An_exhausted_fork_budget_is_refused_uniformly_without_calling_the_forge()
    {
        var repository = PublicRepository();
        var exhausted = new ForkUploadBudget(ForkUploadBudget.PerWindow, DateTime.UtcNow.AddMinutes(-5));
        repository.ForkUploads = exhausted;
        var h = Create(repository, ForkPull());

        ShouldBeTheUniformRefusal(await Upload(h, Form()));
        await h.Forge.DidNotReceiveWithAnyArgs().GetPullRequestAsync(default!, default, default);
        repository.ForkUploads.Should().Be(exhausted);
    }

    [Fact]
    public async Task A_pull_request_the_forge_will_not_return_is_refused_uniformly()
        => ShouldBeTheUniformRefusal(await Upload(Create(PublicRepository(), pull: null), Form()));

    /// <summary>A same-repository pull request has a credential and belongs on the authenticated endpoint.</summary>
    [Fact]
    public async Task A_pull_request_that_is_not_from_a_fork_is_refused_uniformly()
    {
        var h = Create(PublicRepository(), ForkPull(headRepositoryId: BaseRepositoryId));

        ShouldBeTheUniformRefusal(await Upload(h, Form()));
        await h.Ingestor.DidNotReceiveWithAnyArgs().IngestAsync(default!, default);
    }

    /// <summary>The pull request is the credential: a sha that is not its current head is refused, and the budget is untouched.</summary>
    [Fact]
    public async Task A_sha_that_is_not_the_pull_requests_head_is_refused_uniformly()
    {
        var repository = PublicRepository();
        var h = Create(repository, ForkPull());

        ShouldBeTheUniformRefusal(await Upload(h, Form(sha: "0000000000000000000000000000000000000000")));
        repository.ForkUploads.Should().BeNull();
    }

    /// <summary>The head-sha comparison is case-insensitive: a sha is hex, and the forge and git may disagree on case.</summary>
    [Fact]
    public async Task The_head_sha_comparison_ignores_case()
        => (await Upload(Create(PublicRepository(), ForkPull()), Form(sha: HeadSha.ToUpperInvariant()))).Should().BeOfType<AcceptedResult>();
}
