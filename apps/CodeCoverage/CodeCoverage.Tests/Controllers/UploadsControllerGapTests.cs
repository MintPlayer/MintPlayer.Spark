using System.Security.Claims;
using System.Text;
using CodeCoverage.ApiTokens;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The upload endpoint's arms no other file reaches: <c>/finish</c>, the OIDC claim overrides,
/// OIDC auto-provisioning of a public repository, and the input validation.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Auto-provisioning is a write any public GitHub repository's workflow can trigger</b> on the
/// production server: an OIDC token from a repository the app has never seen creates its Repository
/// and Account documents. That is intended, and it rests on exactly one gate — the signed
/// <c>repository_visibility</c> claim — so "private is refused" is pinned here.
/// </para>
/// <para>
/// <c>/finish</c> is what the action calls at the end of every run and was at 0%.
/// </para>
/// </remarks>
public class UploadsControllerGapTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const long RepoId = 6060;
    private const long OwnerId = 606;
    private const string Sha = "6060606060606060606060606060606060606060";

    private static readonly ForgeOidcProfile Oidc = ForgeOidcProfile.GitHub;

    private sealed record Harness(UploadsController Controller, RecordingMessageBus Bus, List<UploadIngestRequest> Ingested);

    private static Harness Create(IAsyncDocumentSession session, ClaimsPrincipal user)
    {
        var bus = new RecordingMessageBus();
        var ingested = new List<UploadIngestRequest>();
        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<IMessageBus>(bus);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var scriptedForge = new CodeCoverage.Tests.Services.ScriptedDiffService();
        services.AddSingleton<IForgeIntegration>(scriptedForge);
        services.AddSingleton<IForgeIntegrationResolver>(scriptedForge);
        services.AddScoped<IBaseResolver, BaseResolver>();
        services.AddScoped<IRepositoryResolver>(sp => new TestRepositoryResolver(sp.GetService<IAsyncDocumentSession>()));
        var ingestor = Substitute.For<IUploadIngestor>();
        ingestor.IngestAsync(Arg.Any<UploadIngestRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            ingested.Add(call.Arg<UploadIngestRequest>());
            return Task.FromResult(new UploadIngestResult("Builds/x", "s-x"));
        });
        services.AddSingleton(ingestor);
        services.AddScoped<UploadsController>();

        var controller = services.BuildServiceProvider().GetRequiredService<UploadsController>();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return new Harness(controller, bus, ingested);
    }

    private static ClaimsPrincipal OidcToken(string fullName, long repositoryId = RepoId, string visibility = "public",
        long? ownerId = OwnerId, string? runId = null, string? runAttempt = null)
    {
        var claims = new List<Claim>
        {
            new(Oidc.RepositoryClaim, fullName),
            new(Oidc.RepositoryIdClaim, repositoryId.ToString()),
            new(Oidc.OwnerClaim, fullName.Split('/')[0]),
            new(Oidc.VisibilityClaim, visibility),
        };
        if (ownerId is { } id) claims.Add(new Claim(Oidc.OwnerIdClaim, id.ToString()));
        if (runId is not null) claims.Add(new Claim(Oidc.RunIdClaim, runId));
        if (runAttempt is not null) claims.Add(new Claim(Oidc.RunAttemptClaim!, runAttempt));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Oidc.SchemeName));
    }

    private static ClaimsPrincipal RepositoryToken(params long[] repositoryIds) => new(new ClaimsIdentity(
        [
            new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Repository"),
            .. repositoryIds.Select(id => new Claim(ApiTokenAuthenticationHandler.RepositoryClaim, Repository.DocumentId(EForgeProvider.GitHub, id))),
        ], ApiTokenAuthenticationHandler.SchemeName));

    private static UploadsController.UploadForm Form(string repository = "acme/widget", int files = 1, string sha = Sha) => new UploadsController.UploadForm
    {
        Repository = repository,
        CommitSha = sha,
        RunId = 1,
        RunAttempt = 1,
        Files = new FormFileCollection(),
    }.WithFiles(files);

    private static async Task SeedRepositoryAsync(IDocumentStore store, string fullName = "acme/widget",
        RepositoryConnection connection = RepositoryConnection.Connected)
    {
        using var seed = store.OpenAsyncSession();
        var repository = new Repository
        {
            GitHubId = RepoId, Name = fullName.Split('/')[1], FullName = fullName, OwnerLogin = fullName.Split('/')[0],
        };
        if (connection == RepositoryConnection.Disconnected)
            repository.MarkDisconnected(DisconnectedReasons.RemovedFromInstallation);
        await seed.StoreAsync(repository, Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        await seed.SaveChangesAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("acme/widget", 0, Sha, "No coverage report files")]
    [InlineData("no-slash", 1, Sha, "repository must be owner/name")]
    [InlineData("", 1, Sha, "repository must be owner/name")]
    [InlineData("acme/widget", 1, "abc12", "commitSha is required")]
    public async Task An_upload_missing_a_required_part_is_a_bad_request(string repository, int files, string sha, string expected)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        var result = await Create(session, OidcToken("acme/widget")).Controller.Upload(Form(repository, files, sha), default);

        Assert.IsType<BadRequestObjectResult>(result.Result).Value!.ToString().Should().Contain(expected);
    }

    /// <summary>A partial upload with nothing to report but a file list is legitimate: the assembler fills it in from the base.</summary>
    [Fact]
    public async Task A_zero_report_partial_upload_with_a_file_list_is_accepted()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        using var session = store.OpenAsyncSession();
        var h = Create(session, OidcToken("acme/widget"));
        var form = Form(files: 0);
        form.Partial = true;
        form.FileList = "src/a.ts\n";

        Assert.IsType<AcceptedResult>((await h.Controller.Upload(form, default)).Result);
        h.Ingested.Single().Partial.Should().BeTrue();
    }

    // ------------------------------------------------------------------------------------------
    // OIDC
    // ------------------------------------------------------------------------------------------

    /// <summary>The signed run id and attempt override the body's, so a workflow cannot attach its coverage to another run.</summary>
    [Fact]
    public async Task The_signed_run_claims_override_the_posted_run()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        using var session = store.OpenAsyncSession();
        var h = Create(session, OidcToken("acme/widget", runId: "987654", runAttempt: "3"));

        await h.Controller.Upload(Form(), default);

        var request = h.Ingested.Single();
        request.RunId.Should().Be(987654);
        request.RunAttempt.Should().Be(3);
        request.ContributedFromFork.Should().BeFalse();
    }

    /// <summary>A workflow can only ever upload for the repository it runs in.</summary>
    [Fact]
    public async Task An_oidc_token_cannot_upload_for_a_repository_other_than_its_own()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        using var session = store.OpenAsyncSession();
        var h = Create(session, OidcToken("someone/else"));

        Assert.IsType<NotFoundObjectResult>((await h.Controller.Upload(Form(), default)).Result);
        h.Ingested.Should().BeEmpty();
    }

    [Fact]
    public async Task A_public_repository_the_app_has_never_seen_is_provisioned_with_its_account()
    {
        using var store = GetDocumentStore();
        using (var session = store.OpenAsyncSession())
        {
            var h = Create(session, OidcToken("newcomer/lib"));
            Assert.IsType<AcceptedResult>((await h.Controller.Upload(Form("newcomer/lib"), default)).Result);
            await session.SaveChangesAsync(); // the real ingestor's save; the substitute has none
        }

        using var check = store.OpenAsyncSession();
        var repository = await check.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        repository.FullName.Should().Be("newcomer/lib");
        repository.OwnerLogin.Should().Be("newcomer");
        repository.IsPrivate.Should().BeFalse();
        repository.Account.Should().Be(Account.DocumentId(EForgeProvider.GitHub, OwnerId));
        (await check.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, OwnerId))).Login.Should().Be("newcomer");
    }

    /// <summary>An existing account is reused, not duplicated.</summary>
    [Fact]
    public async Task Provisioning_reuses_an_account_that_already_exists()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = OwnerId, Login = "newcomer", AvatarUrl = "https://example.invalid/a.png" },
                Account.DocumentId(EForgeProvider.GitHub, OwnerId));
            await seed.SaveChangesAsync();
        }
        using (var session = store.OpenAsyncSession())
        {
            await Create(session, OidcToken("newcomer/lib")).Controller.Upload(Form("newcomer/lib"), default);
            await session.SaveChangesAsync();
        }

        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, OwnerId))).AvatarUrl
            .Should().Be("https://example.invalid/a.png");
    }

    /// <summary>Without an owner id there is no account to key on, so the repository is provisioned unattached.</summary>
    [Fact]
    public async Task Provisioning_without_an_owner_id_creates_no_account()
    {
        using var store = GetDocumentStore();
        using (var session = store.OpenAsyncSession())
        {
            await Create(session, OidcToken("newcomer/lib", ownerId: null)).Controller.Upload(Form("newcomer/lib"), default);
            await session.SaveChangesAsync();
        }

        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, RepoId))).Account.Should().BeNull();
        (await check.Query<Account>().CountAsync()).Should().Be(0);
    }

    /// <summary>The one gate on auto-provisioning. A private (or internal) repository's token provisions nothing.</summary>
    [Theory]
    [InlineData("private")]
    [InlineData("internal")]
    [InlineData("Public")] // compared exactly, by design
    public async Task A_non_public_repository_is_never_auto_provisioned(string visibility)
    {
        using var store = GetDocumentStore();
        using (var session = store.OpenAsyncSession())
        {
            var h = Create(session, OidcToken("secret/lib", visibility: visibility));
            Assert.IsType<NotFoundObjectResult>((await h.Controller.Upload(Form("secret/lib"), default)).Result);
            await session.SaveChangesAsync();
            h.Ingested.Should().BeEmpty();
        }

        using var check = store.OpenAsyncSession();
        (await check.Query<Repository>().CountAsync()).Should().Be(0);
        (await check.Query<Account>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_oidc_token_without_a_numeric_repository_id_is_refused()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(Oidc.RepositoryClaim, "acme/widget"), new Claim(Oidc.RepositoryIdClaim, "not-a-number")], Oidc.SchemeName));

        Assert.IsType<NotFoundObjectResult>((await Create(session, principal).Controller.Upload(Form(), default)).Result);
    }

    /// <summary>
    /// A workflow that still uploads proves the repository is alive: an upload reconnects it, and
    /// takes the signed name as the current one, remembering the old.
    /// </summary>
    [Fact]
    public async Task An_oidc_upload_reconnects_and_renames_a_known_repository()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store, "acme/old-name", RepositoryConnection.Disconnected);
        using (var session = store.OpenAsyncSession())
        {
            Assert.IsType<AcceptedResult>((await Create(session, OidcToken("acme/new-name")).Controller.Upload(Form("acme/new-name"), default)).Result);
            await session.SaveChangesAsync();
        }

        using var check = store.OpenAsyncSession();
        var repository = await check.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        repository.Connection.Should().Be(RepositoryConnection.Connected);
        repository.FullName.Should().Be("acme/new-name");
        repository.Name.Should().Be("new-name");
        repository.PreviousFullNames.Should().Equal("acme/old-name");
    }

    /// <summary>A poll must not mutate: the status GET resolves an OIDC repository without reconnecting or provisioning it.</summary>
    [Fact]
    public async Task A_status_poll_neither_reconnects_nor_provisions()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store, "acme/widget", RepositoryConnection.Disconnected);
        using (var session = store.OpenAsyncSession())
        {
            var known = await Create(session, OidcToken("acme/widget")).Controller.Status("acme/widget", Sha, runId: 1, runAttempt: 1, default);
            var unknown = await Create(session, OidcToken("newcomer/lib", repositoryId: 7777)).Controller.Status("newcomer/lib", Sha, runId: 1, runAttempt: 1, default);
            known.Result.Should().BeOfType<NotFoundObjectResult>("the repository resolved; only the build is missing");
            unknown.Result.Should().BeOfType<NotFoundResult>("an unknown repository is not provisioned by a poll");
            await session.SaveChangesAsync();
        }

        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, RepoId))).Connection
            .Should().Be(RepositoryConnection.Disconnected);
        (await check.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, 7777))).Should().BeNull();
    }

    [Theory]
    [InlineData("", Sha)]
    [InlineData("no-slash", Sha)]
    [InlineData("acme/widget", "")]
    public async Task A_status_poll_missing_a_required_part_is_a_bad_request(string repository, string sha)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        var result = await Create(session, OidcToken("acme/widget")).Controller.Status(repository, sha, runId: 1, runAttempt: 1, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ------------------------------------------------------------------------------------------
    // Repository-scoped tokens
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_repository_scoped_token_uploads_only_to_the_repositories_it_names()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();

        Assert.IsType<AcceptedResult>((await Create(session, RepositoryToken(1, RepoId)).Controller.Upload(Form(), default)).Result);
        Assert.IsType<NotFoundObjectResult>((await Create(session, RepositoryToken(1)).Controller.Upload(Form(), default)).Result);
    }

    [Fact]
    public async Task A_token_of_an_unknown_scope_authorizes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Everything")], ApiTokenAuthenticationHandler.SchemeName));

        Assert.IsType<NotFoundObjectResult>((await Create(session, principal).Controller.Upload(Form(), default)).Result);
        Assert.IsType<NotFoundObjectResult>((await Create(session, principal).Controller.Upload(Form("a/b/c"), default)).Result);
    }

    // ------------------------------------------------------------------------------------------
    // /finish
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Finish_queues_the_runs_build_for_finalization()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        var buildId = Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, 11, 2);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Build { Commit = Commit.DocumentId(EForgeProvider.GitHub, RepoId, Sha), CiRunId = 11, CiRunAttempt = 2 }, buildId);
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        var h = Create(session, OidcToken("acme/widget"));

        var result = await h.Controller.Finish(new UploadsController.FinishRequest("acme/widget", Sha, 11, 2), default);

        Assert.IsType<AcceptedResult>(result);
        h.Bus.Of<FinalizeBuildMessage>().Single().BuildId.Should().Be(buildId);
    }

    [Fact]
    public async Task Finish_for_an_unknown_run_or_repository_is_not_found_and_queues_nothing()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store);
        using var session = store.OpenAsyncSession();
        var h = Create(session, OidcToken("acme/widget"));

        Assert.IsType<NotFoundResult>(await h.Controller.Finish(new UploadsController.FinishRequest("acme/widget", Sha, 99, 1), default));
        Assert.IsType<NotFoundResult>(await h.Controller.Finish(new UploadsController.FinishRequest("other/repo", Sha, 11, 2), default));
        h.Bus.Messages.Should().BeEmpty();
    }
}

internal static class UploadFormExtensions
{
    public static UploadsController.UploadForm WithFiles(this UploadsController.UploadForm form, int count)
    {
        var files = new FormFileCollection();
        for (var i = 0; i < count; i++)
            files.Add(new FormFile(new MemoryStream(Encoding.UTF8.GetBytes("TN:\nSF:a.ts\nDA:1,1\nend_of_record\n")), 0, 34, "files", $"r{i}.info"));
        form.Files = files;
        return form;
    }
}
