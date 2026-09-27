using System.Net;
using System.Text;
using System.Text.Json;
using CodeCoverage.Forge;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The first tests in this application that actually execute its <c>[SparkAuthorize]</c> filters.
/// <para>
/// Everything else constructs a controller through DI and calls the method, which cannot run a
/// filter — so the attribute protecting six production files was, until now, enforced by nothing
/// in this suite. Both halves matter and only one of them is usually remembered: that a caller
/// without the right is refused, AND that an <c>[AllowAnonymous]</c> endpoint is still reachable.
/// The badge is public by design, and a change that made the whole app require a signed-in user
/// would break every README badge in the wild while every existing test stayed green.
/// </para>
/// </summary>
/// <remarks>
/// These were skipped for one session with a note claiming the model hash was unstable across
/// hosting models. It was: booting in-process threw <c>SparkModelOutOfSyncException</c> for exactly
/// Account, Build and Repository while <c>--spark-verify-model</c> on the same build reported the
/// model in sync. The cause was <c>Assembly.GetEntryAssembly()</c> seeding index discovery — under
/// a test host that is the test runner, not the application, so the catalog came up empty and the
/// querytype/index lines vanished from every projection-backed entity's shape. Fixed in
/// <c>SparkExtensions.UseContext</c>, which now anchors discovery on the context assembly.
/// <para>
/// The host is shared via <see cref="CoverageWebHostCollection"/>. <b>Do not construct a factory per
/// test:</b> Spark's registry, index catalog and model loader are process-wide, and concurrent
/// boots throw "Collection was modified; enumeration operation may not execute".
/// </para>
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class SparkAuthorizeEndToEndTests
{
    private readonly CoverageWebHostFixture fixture;

    public SparkAuthorizeEndToEndTests(CoverageWebHostFixture fixture) => this.fixture = fixture;

    private HttpClient CreateClient() => fixture.Factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Booting the real composition root is itself the assertion. <c>Program.cs</c> is 291 lines
    /// that no test had ever executed, and the failure modes it hides are startup-only: a missing
    /// DI registration, a middleware ordering violation, a security.json that no longer matches the
    /// model. None of those can be caught by testing a controller in isolation.
    /// </summary>
    [Fact]
    public void The_application_starts()
    {
        using var client = CreateClient();

        Assert.NotNull(client);
    }

    /// <summary>
    /// The badge endpoint carries [AllowAnonymous], which beats the type-level [SparkAuthorize].
    /// Anonymous access is the entire point of a coverage badge.
    /// </summary>
    [Fact]
    public async Task An_anonymous_badge_request_is_not_challenged()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/badge/github/acme/does-not-exist.svg");

        // 404, or an "unknown" badge, are both fine. What must NOT happen is 401/403 — that would
        // mean authorization is being applied to a deliberately public endpoint.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The other half: an endpoint that is NOT anonymous must refuse an anonymous caller. With
    /// GitHub as the only provider a challenge is a redirect rather than a bare 401, so both shapes
    /// are accepted — what is asserted is that the request does not simply succeed.
    /// </summary>
    [Fact]
    public async Task An_anonymous_caller_cannot_reach_an_authorized_endpoint()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/me/accounts");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.Found
                or HttpStatusCode.Redirect,
            $"an anonymous caller reached /api/me/accounts and got {(int)response.StatusCode} "
            + $"{response.StatusCode}; the authorization filter did not run.");
    }

    /// <summary>
    /// Settings management is not anonymous either. Worth its own case because the controller
    /// carries <c>[SparkAuthorize]</c> at the TYPE level rather than per method — a refactor that
    /// moved the attribute onto individual actions could leave one uncovered, and unit tests of the
    /// methods would never notice, because a method call runs no filter.
    /// </summary>
    /// <remarks>
    /// <c>/api/tokens</c> used to be a case here and is gone: upload tokens are a Spark persistent
    /// object now, so there is no tokens controller to carry an attribute. What replaced this
    /// coverage is not another route test but two different things — the type-level grant in
    /// <c>security.json</c>, which is <c>Authenticated</c>, and <c>ApiTokenActions</c>&apos; row
    /// filter, which is what keeps one signed-in user out of another&apos;s tokens.
    /// </remarks>
    /// <remarks>
    /// <c>settings/gate</c> used to be the case here and is gone the same way: the gate is edited
    /// through the Spark PO form on <c>Repository</c> now (#413), so there are no gate endpoints to
    /// carry the attribute. What replaced that coverage is <c>Edit/Repository</c> in
    /// <c>security.json</c> plus <c>RepositoryActions.GetRowFilterAsync</c>&apos;s write arm, which
    /// is what keeps a signed-in user out of a repository they do not manage.
    /// <para>
    /// <c>badge-token</c> is a POST, so this probes it as one. A GET would answer 405 before any
    /// authorization filter ran, which would pass the assertion while proving nothing.
    /// </para>
    /// </remarks>
    [Theory]
    // Provider-segmented since M7/D27: the forge precedes the owner.
    [InlineData("/api/repos/github/acme/widget/settings/badge-token")]
    public async Task Management_endpoints_refuse_anonymous_callers(string path)
    {
        using var client = CreateClient();

        var response = await client.PostAsync(path, content: null);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.Found
                or HttpStatusCode.Redirect,
            $"{path} answered {(int)response.StatusCode} to an anonymous caller.");
    }

    // ----------------------------------------------------------------------------------
    // #453 — anonymous visitors on public pages, and private ≡ missing through the pipeline
    // ----------------------------------------------------------------------------------

    private const long ParityPrivateId = 453_000;
    private const long ParityPublicId = 453_001;

    /// <summary>
    /// Seeds an installed owner with one private and one public repository. Idempotent: every test
    /// in this class shares the host and its database, so each one may seed and none may depend on
    /// running first.
    /// </summary>
    private async Task SeedParityAsync()
    {
        using (var seed = fixture.Store.OpenAsyncSession())
        {
            await seed.StoreAsync(
                new Entities.Account { GitHubId = 45300, Provider = EForgeProvider.GitHub, Login = "parity-org", Type = "Organization" },
                Entities.Account.DocumentId(EForgeProvider.GitHub, 45300));
            await seed.StoreAsync(new Entities.Repository
            {
                GitHubId = ParityPrivateId, Provider = EForgeProvider.GitHub, Name = "secret",
                FullName = "parity-org/secret", OwnerLogin = "parity-org", IsPrivate = true,
                PreviousFullNames = ["parity-org/old-secret"],
            }, Entities.Repository.DocumentId(EForgeProvider.GitHub, ParityPrivateId));
            await seed.StoreAsync(new Entities.Repository
            {
                GitHubId = ParityPublicId, Provider = EForgeProvider.GitHub, Name = "open",
                FullName = "parity-org/open", OwnerLogin = "parity-org", IsPrivate = false,
            }, Entities.Repository.DocumentId(EForgeProvider.GitHub, ParityPublicId));
            await seed.SaveChangesAsync();
        }
        fixture.WaitForIndexing();
    }

    /// <summary>
    /// The bug as filed: the Browse API 401'd every anonymous caller before security.json was
    /// consulted, because <c>[SparkAuthorize]</c> had no policy and so inherited ASP.NET Core's
    /// require-authenticated default — and the SPA's interceptor turned that 401 into a sign-in
    /// redirect off a public page. security.json grants anonymous <c>Browse/Coverage</c>.
    /// </summary>
    [Theory]
    [InlineData("/api/browse/repos/github/parity-org/open")]
    [InlineData("/api/browse/repos/github/parity-org/open/history")]
    [InlineData("/api/browse/repos/github/parity-org/open/branches")]
    public async Task An_anonymous_visitor_can_browse_a_public_repository(string path)
    {
        await SeedParityAsync();
        using var client = CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} is public and anonymous holds Browse/Coverage");
    }

    /// <summary>
    /// The public Repository detail page needs the <c>DeleteBranchPolicy</c> labels. Anonymous holds
    /// no <c>Read/LookupReferences</c>, but may Read Repository, which binds that lookup.
    /// </summary>
    [Fact]
    public async Task An_anonymous_visitor_can_read_a_lookup_bound_by_a_readable_type()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/spark/lookupref/DeleteBranchPolicy");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/api/browse/repos/github/parity-org/old-secret")]  // private, remembered alias
    [InlineData("/api/browse/repos/github/parity-org/made-up")]     // missing, known owner
    [InlineData("/api/browse/repos/github/parity-stranger/x")]      // unknown owner
    [InlineData("/api/browse/repos/nosuchforge/parity-org/secret")] // unknown forge
    [InlineData("/api/browse/repos/github/parity-org/secret/history")]
    public async Task Anonymous_browse_answers_a_private_repository_exactly_like_a_missing_one(string path)
    {
        await SeedParityAsync();
        using var client = CreateClient();

        var reference = await client.GetAsync("/api/browse/repos/github/parity-org/secret");
        var response = await client.GetAsync(path);

        // 401 is load-bearing: it is what the SPA turns into "sign in to see this", and it is the
        // same answer /spark/po/load gives. What matters for #453 is that it is the SAME answer.
        reference.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().Be(reference.StatusCode, path);
        (await response.Content.ReadAsStringAsync()).Should().Be(await reference.Content.ReadAsStringAsync(), path);
        response.Content.Headers.ContentType?.ToString().Should().Be(reference.Content.Headers.ContentType?.ToString(), path);
        response.Headers.WwwAuthenticate.ToString().Should().Be(reference.Headers.WwwAuthenticate.ToString(), path);
    }

    [Fact]
    public async Task Anonymous_po_load_answers_a_private_id_exactly_like_a_missing_one()
    {
        await SeedParityAsync();
        using var client = CreateClient();

        async Task<(HttpStatusCode, string)> Load(string id)
        {
            var response = await client.PostAsync("/spark/po/load", new StringContent(
                JsonSerializer.Serialize(new { objectTypeId = RepositoryTypeId, id }), Encoding.UTF8, "application/json"));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var missing = await Load(Entities.Repository.DocumentId(EForgeProvider.GitHub, 453_999));
        var @private = await Load(Entities.Repository.DocumentId(EForgeProvider.GitHub, ParityPrivateId));

        @private.Should().Be(missing, "a private repository's id must not be distinguishable from an unused one");
    }

    /// <summary>Repository's model id (<c>App_Data/Model/Repository.json</c>).</summary>
    private const string RepositoryTypeId = "22880468-80f5-4fd2-9472-4f87842ce4ff";

    /// <summary>Commit's model id (<c>App_Data/Model/Commit.json</c>).</summary>
    private const string CommitTypeId = "ec83c3f7-45d7-44e8-928a-2daf9a4b3ffe";

    /// <summary>Build's model id (<c>App_Data/Model/Build.json</c>).</summary>
    private const string BuildTypeId = "2487903b-6dc7-4540-a99c-fc94c5d92551";

    private const string ParitySha = "453c0ffee453c0ffee453c0ffee453c0ffee4530";

    /// <summary>A commit and a build under both the private and the public parity repository.</summary>
    private async Task SeedParityCommitsAsync()
    {
        await SeedParityAsync();
        using (var seed = fixture.Store.OpenAsyncSession())
        {
            foreach (var repo in new[] { ParityPrivateId, ParityPublicId })
            {
                var commitId = Entities.Commit.DocumentId(EForgeProvider.GitHub, repo, ParitySha);
                await seed.StoreAsync(new Entities.Commit
                {
                    Repository = Entities.Repository.DocumentId(EForgeProvider.GitHub, repo),
                    Sha = ParitySha,
                    Message = "parity",
                }, commitId);
                await seed.StoreAsync(new Entities.Build
                {
                    Commit = commitId, CiRunId = 1, CiRunAttempt = 1, Run = Entities.Build.ComposeRun(1, 1),
                }, Entities.Build.DocumentId(EForgeProvider.GitHub, repo, ParitySha, 1, 1));
            }
            await seed.SaveChangesAsync();
        }
        fixture.WaitForIndexing();
    }

    /// <summary>
    /// <c>security.json</c> grants anonymous <c>QueryRead/Commit</c> and <c>QueryRead/Build</c>, so the
    /// only thing between an anonymous caller and a private repository's commits and builds is the
    /// row rule — <c>CommitActions.GetRowFilterAsync</c> and <c>BuildActions.IsAllowedAsync</c> over
    /// the real <c>SparkVisibility</c>. Through the pipeline, the private id must be byte-identical to
    /// an unused one; the public id loading proves the comparison is not two identical refusals of
    /// everything.
    /// </summary>
    [Theory]
    [InlineData(CommitTypeId, "commit")]
    [InlineData(BuildTypeId, "build")]
    public async Task Anonymous_po_load_answers_a_private_commit_or_build_exactly_like_a_missing_one(string typeId, string kind)
    {
        await SeedParityCommitsAsync();
        using var client = CreateClient();

        string IdFor(long repo, string sha) => kind == "commit"
            ? Entities.Commit.DocumentId(EForgeProvider.GitHub, repo, sha)
            : Entities.Build.DocumentId(EForgeProvider.GitHub, repo, sha, 1, 1);

        async Task<(HttpStatusCode, string)> Load(string id)
        {
            var response = await client.PostAsync("/spark/po/load", new StringContent(
                JsonSerializer.Serialize(new { objectTypeId = typeId, id }), Encoding.UTF8, "application/json"));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var missing = await Load(IdFor(ParityPrivateId, "0000000000000000000000000000000000000000"));
        var @private = await Load(IdFor(ParityPrivateId, ParitySha));
        var @public = await Load(IdFor(ParityPublicId, ParitySha));

        @private.Should().Be(missing, $"a private repository's {kind} must not be distinguishable from an unused id");
        @public.Item1.Should().Be(HttpStatusCode.OK, $"a public repository's {kind} is anonymously readable");
    }
}
