using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeCoverage.ApiTokens;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The first AUTHENTICATED requests through this application's pipeline, and the anonymous fork
/// endpoint end to end.
/// </summary>
/// <remarks>
/// <para>
/// Until these, every authenticated path was tested by calling a controller method with a
/// hand-built principal, which runs no authentication handler, no <c>[Authorize]</c> scheme
/// selection, no <c>[SparkAuthorize]</c> right, and no rate-limiter partition. Here a real
/// <c>covt_</c> token is seeded — only its hash is stored, exactly as the app stores one — and sent
/// as a bearer credential, so <c>ApiTokenAuthenticationHandler</c>, the <c>Upload/Coverage</c>
/// right, <c>UploadsPartitionKey</c> and the real ingestor all run.
/// </para>
/// <para>
/// The fork upload runs against the host whose GitHub is <see cref="StubGitHub"/>: the endpoint
/// reads the pull request back from GitHub before accepting anything, and that read travels the
/// app's own forge client and Octokit to the stub.
/// </para>
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class UploadsHttpTests
{
    private readonly CoverageWebHostFixture host;
    private readonly StubbedGitHubWebHostFixture stubbed;

    public UploadsHttpTests(CoverageWebHostFixture host, StubbedGitHubWebHostFixture stubbed)
    {
        this.host = host;
        this.stubbed = stubbed;
    }

    private static HttpClient Client(CoverageWebAppFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private const long AccountGitHubId = 77_700;
    private const long RepoGitHubId = 77_701;
    private const string Sha = "7777777777777777777777777777777777777777";

    /// <summary>A plaintext token that exists only in this test; the database holds its hash.</summary>
    private static readonly string TokenValue = ApiTokenService.GenerateTokenValue();

    private async Task SeedTokenAsync()
    {
        using (var seed = host.Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = AccountGitHubId, Login = "http-org", Type = "Organization" },
                Account.DocumentId(EForgeProvider.GitHub, AccountGitHubId));
            await seed.StoreAsync(new Repository
            {
                GitHubId = RepoGitHubId, Name = "widget", FullName = "http-org/widget", OwnerLogin = "http-org",
                Account = Account.DocumentId(EForgeProvider.GitHub, AccountGitHubId),
            }, Repository.DocumentId(EForgeProvider.GitHub, RepoGitHubId));
            await seed.StoreAsync(new ApiToken
            {
                Hash = ApiTokenService.Hash(TokenValue),
                Scope = "Account",
                Description = "http-test",
                AccountLogin = "http-org",
                AccountOwnerKey = new ForgeOwner(EForgeProvider.GitHub, "http-org").ToString(),
                AccountId = AccountGitHubId,
                Provider = EForgeProvider.GitHub,
                CreatedAtUtc = DateTime.UtcNow,
            }, "ApiTokens/http-test");
            await seed.SaveChangesAsync();
        }

        // The handler's token lookup runs on an auto-index; query once so it exists, then wait.
        using (var warm = host.Store.OpenAsyncSession())
            await warm.Query<ApiToken>().Where(t => t.Hash == "warm-up").ToListAsync();
        host.WaitForIndexing();
    }

    private static MultipartFormDataContent UploadForm(string? repository, string sha, int files = 1, long runId = 5001)
    {
        var form = new MultipartFormDataContent();
        if (repository is not null) form.Add(new StringContent(repository), "repository");
        form.Add(new StringContent(sha), "commitSha");
        form.Add(new StringContent(runId.ToString()), "runId");
        form.Add(new StringContent("1"), "runAttempt");
        form.Add(new StringContent("main"), "branch");
        for (var i = 0; i < files; i++)
        {
            var report = new ByteArrayContent(Encoding.UTF8.GetBytes("TN:\nSF:src/a.ts\nDA:1,1\nDA:2,0\nend_of_record\n"));
            report.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(report, "files", $"lcov{i}.info");
        }
        return form;
    }

    [Fact]
    public async Task A_seeded_upload_token_uploads_finishes_and_polls_through_the_pipeline()
    {
        await SeedTokenAsync();
        using var client = Client(host.Factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenValue);

        var upload = await client.PostAsync("/api/uploads", UploadForm("http-org/widget", Sha));
        var uploadBody = await upload.Content.ReadAsStringAsync();
        upload.StatusCode.Should().Be(HttpStatusCode.Accepted, uploadBody);
        var buildId = JsonDocument.Parse(uploadBody).RootElement.GetProperty("buildId").GetString();
        buildId.Should().Be(Build.DocumentId(EForgeProvider.GitHub, RepoGitHubId, Sha, 5001, 1));

        var finish = await client.PostAsJsonAsync("/api/uploads/finish",
            new { repository = "http-org/widget", commitSha = Sha, runId = 5001, runAttempt = 1 });
        finish.StatusCode.Should().Be(HttpStatusCode.Accepted, await finish.Content.ReadAsStringAsync());

        var status = await client.GetAsync($"/api/uploads/status?repository=http-org/widget&commitSha={Sha}&runId=5001&runAttempt=1");
        status.StatusCode.Should().Be(HttpStatusCode.OK, await status.Content.ReadAsStringAsync());

        // Stored by the real ingestor, under the token's repository.
        using var check = host.Store.OpenAsyncSession();
        (await check.LoadAsync<Build>(buildId)).Should().NotBeNull();
    }

    /// <summary>
    /// An authenticated caller is still scoped: the token authorizes its own account's
    /// repositories, and anything else is the same bare 404 as a repository that does not exist.
    /// </summary>
    [Fact]
    public async Task A_seeded_token_cannot_upload_outside_its_account()
    {
        await SeedTokenAsync();
        using var client = Client(host.Factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenValue);

        var response = await client.PostAsync("/api/uploads", UploadForm("someone-else/widget", Sha));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null)]                    // no credential at all
    [InlineData("covt_not-a-real-token")] // a well-formed token that does not exist
    public async Task An_upload_without_a_valid_credential_is_refused_before_the_controller(string? token)
    {
        using var client = Client(host.Factory);
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/api/uploads", UploadForm("http-org/widget", Sha));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_health_endpoints_answer_without_a_GitHub_App_configured()
    {
        using var client = Client(host.Factory);

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("status").GetString().Should().Be("ready");
        body.GetProperty("gitHubApp").GetProperty("status").GetString().Should().Be("skipped");
    }

    // ------------------------------------------------------------------------------------------
    // The anonymous fork endpoint
    // ------------------------------------------------------------------------------------------

    private const string UniformRefusal = "No such pull request, or coverage cannot be accepted for it.";

    private static MultipartFormDataContent ForkForm(string sha, int files = 1)
    {
        var form = new MultipartFormDataContent { { new StringContent(sha), "commitSha" }, { new StringContent("42"), "runId" } };
        for (var i = 0; i < files; i++)
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("TN:\nSF:src/a.ts\nDA:1,1\nend_of_record\n")), "files", $"lcov{i}.info");
        return form;
    }

    /// <summary>
    /// Refused before any forge call, anonymously, and every refusal is the same 404 — through the
    /// rate limiter's partition function and real form binding.
    /// </summary>
    [Theory]
    [InlineData("/api/uploads/fork/github/nobody/nothing/pull/1", 1)]    // unknown repository
    [InlineData("/api/uploads/fork/nosuchforge/nobody/nothing/pull/1", 1)] // unknown forge
    [InlineData("/api/uploads/fork/github/nobody/empty/pull/1", 0)]      // no report files
    public async Task A_fork_upload_that_cannot_be_accepted_is_the_uniform_404(string path, int files)
    {
        using var client = Client(host.Factory);

        var response = await client.PostAsync(path, ForkForm(Sha, files));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain(UniformRefusal);
    }

    [Fact]
    public async Task A_fork_upload_with_too_many_files_is_a_bad_request()
    {
        using var client = Client(host.Factory);

        var response = await client.PostAsync("/api/uploads/fork/github/nobody/many/pull/1", ForkForm(Sha, files: 65));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private const long ForkInstallationId = 88_800;
    private const long ForkTargetRepoId = 88_801;
    private const string ForkHead = "8888888888888888888888888888888888888888";

    /// <summary>
    /// The whole anonymous path: the repository is public and installed, the pull request is read
    /// back from GitHub (the stub) through the app's own forge client, it is a fork, its head
    /// matches — and the upload is stored as fork-contributed.
    /// </summary>
    [Fact]
    public async Task A_fork_upload_whose_pull_request_checks_out_is_accepted_end_to_end()
    {
        using (var seed = stubbed.Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = 888, Login = "fork-target", InstallationId = ForkInstallationId },
                Account.DocumentId(EForgeProvider.GitHub, 888));
            await seed.StoreAsync(new Repository
            {
                GitHubId = ForkTargetRepoId, Name = "widget", FullName = "fork-target/widget", OwnerLogin = "fork-target",
                Account = Account.DocumentId(EForgeProvider.GitHub, 888), IsPrivate = false,
            }, Repository.DocumentId(EForgeProvider.GitHub, ForkTargetRepoId));
            await seed.SaveChangesAsync();
        }
        stubbed.WaitForIndexing();
        stubbed.GitHub.On(HttpMethod.Get, "/repos/fork-target/widget/pulls/7", HttpStatusCode.OK, $$"""
            {
              "id": 700007, "number": 7, "state": "open", "title": "A contribution",
              "user": { "login": "contributor", "id": 9, "type": "User" },
              "head": { "label": "contributor:patch-1", "ref": "patch-1", "sha": "{{ForkHead}}",
                        "repo": { "id": 99999, "name": "widget", "full_name": "contributor/widget", "default_branch": "main", "private": false,
                                  "owner": { "login": "contributor", "id": 9, "type": "User" } } },
              "base": { "label": "fork-target:main", "ref": "main", "sha": "0000000000000000000000000000000000000000",
                        "repo": { "id": {{ForkTargetRepoId}}, "name": "widget", "full_name": "fork-target/widget", "default_branch": "main", "private": false,
                                  "owner": { "login": "fork-target", "id": 888, "type": "User" } } }
            }
            """);
        using var client = Client(stubbed.Factory);

        var response = await client.PostAsync("/api/uploads/fork/github/fork-target/widget/pull/7", ForkForm(ForkHead));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        stubbed.GitHub.InstallationClients.Should().Contain(ForkInstallationId);
        using var check = stubbed.Store.OpenAsyncSession();
        var commit = await check.LoadAsync<Commit>(Commit.DocumentId(EForgeProvider.GitHub, ForkTargetRepoId, ForkHead, pullRequestNumber: 7));
        commit.Should().NotBeNull();
        commit.ContributedFromFork.Should().BeTrue();
        commit.Branch.Should().Be("patch-1");
    }
}
