using CodeCoverage.Forge;
using System.Security.Claims;
using CodeCoverage.ApiTokens;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// Who may upload for a repository, and what an upload does to a disconnected one.
/// <para>
/// Both behaviours were designed and neither was tested. The authorization one matters most: an
/// account-scoped token used to be compared against <c>Repository.OwnerLogin</c>, a string GitHub
/// lets people change and transfer out from under. After a transfer that comparison is wrong in
/// both directions — the previous owner's token still matches, the new owner's does not.
/// </para>
/// </summary>
public class UploadsControllerAuthorizationTests : CoverageRavenTest
{
    private const long RepoId = 5150;
    private const long OwnerId = 700;
    private const long OtherOwnerId = 800;

    private sealed class NullMessageBus : IMessageBus
    {
        public Task BroadcastAsync<TMessage>(TMessage m, CancellationToken c = default) => Task.CompletedTask;
        public Task BroadcastOnceAsync<TMessage>(TMessage m, string key, CancellationToken c = default) => Task.CompletedTask;
        public Task DelayBroadcastAsync<TMessage>(TMessage m, TimeSpan d, CancellationToken c = default) => Task.CompletedTask;
    }

    private static ClaimsPrincipal AccountToken(string login, long? accountId) =>
        new(new ClaimsIdentity(
            accountId is null
                ? [
                    new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Account"),
                    new Claim(ApiTokenAuthenticationHandler.AccountClaim, ForgeOwner.KeyFromUnqualifiedLogin(login)),
                  ]
                : [
                    new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Account"),
                    new Claim(ApiTokenAuthenticationHandler.AccountClaim, ForgeOwner.KeyFromUnqualifiedLogin(login)),
                    new Claim(ApiTokenAuthenticationHandler.AccountIdClaim, accountId.Value.ToString()),
                    // ⚠️ The handler emits these two together and the controller now requires both: a
                    // numeric account id is unique only WITHIN a forge, so an id with no provider is
                    // ambiguous and fails closed rather than defaulting to GitHub.
                    new Claim(ApiTokenAuthenticationHandler.ProviderClaim, EForgeProvider.GitHub.ToCanonicalString()),
                  ],
            ApiTokenAuthenticationHandler.SchemeName));

    private static ClaimsPrincipal OidcToken(string fullName, long repositoryId) =>
        new(new ClaimsIdentity(
            [
                new Claim(GitHubOidc.Profile.RepositoryClaim, fullName),
                new Claim(GitHubOidc.Profile.RepositoryIdClaim, repositoryId.ToString()),
                new Claim(GitHubOidc.Profile.OwnerClaim, fullName.Split('/')[0]),
                new Claim(GitHubOidc.Profile.VisibilityClaim, "public"),
            ], GitHubOidc.SchemeName));

    private static UploadsController CreateController(IAsyncDocumentSession session, ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<IMessageBus>(new NullMessageBus());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var scriptedForge = new Services.ScriptedDiffService();
        services.AddSingleton<IForgeIntegration>(scriptedForge);
        services.AddSingleton<IForgeIntegrationResolver>(scriptedForge);
        services.AddScoped<IBaseResolver, BaseResolver>();
        services.AddScoped<IRepositoryResolver>(sp => new TestRepositoryResolver(sp.GetService<IAsyncDocumentSession>()));
        services.AddScoped<CodeCoverage.Ingestion.IUploadIngestor, CodeCoverage.Ingestion.UploadIngestor>();
        services.AddScoped<UploadsController>();

        var controller = services.BuildServiceProvider().GetRequiredService<UploadsController>();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    /// <summary>The repository has been transferred: owned by <paramref name="ownerId"/> now.</summary>
    private static async Task SeedAsync(IAsyncDocumentSession session, long ownerId, string ownerLogin,
        RepositoryConnection connection = RepositoryConnection.Connected)
    {
        await session.StoreAsync(new Account { GitHubId = ownerId, Login = ownerLogin }, Account.DocumentId(EForgeProvider.GitHub, ownerId));
        await session.StoreAsync(new Repository
        {
            GitHubId = RepoId,
            Account = Account.DocumentId(EForgeProvider.GitHub, ownerId),
            Name = "widgets",
            FullName = $"{ownerLogin}/widgets",
            OwnerLogin = ownerLogin,
            Connection = connection,
            DisconnectedReason = connection == RepositoryConnection.Disconnected
                ? DisconnectedReasons.TransferredAway : null,
        }, Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// Asserted through the upload rather than through <c>Status</c>: both answer NotFound when the
    /// caller is not authorized — deliberately indistinguishable from "unknown", so a token cannot
    /// be used as an existence oracle — but Status <em>also</em> answers NotFound when there is
    /// simply no build for that sha yet, which would make an unseeded repository look unauthorized.
    /// The upload has no such second meaning: past the resolve it gets on with the upload.
    /// </summary>
    private static async Task<bool> IsAuthorizedAsync(UploadsController controller, string owner)
    {
        var result = await UploadAsync(controller, $"{owner}/widgets");
        return result.Result is not NotFoundObjectResult and not NotFoundResult;
    }

    /// <summary>
    /// ⚠️ The legacy fallback must not union forges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A token minted before <c>AccountId</c> existed still authorizes by owner rather than by id,
    /// so that no working credential is invalidated by a deploy. That fallback compared a <b>bare
    /// login</b> until 2026-09-22 — which was correct while GitHub was the only forge and silently
    /// wrong the moment it was not: a GitLab group called <c>acme</c> and a GitHub organisation
    /// called <c>acme</c> are different principals, and the comparison could not tell them apart.
    /// </para>
    /// <para>
    /// Both sides are qualified now. The claim carries <c>ApiToken.AccountOwnerKey</c>
    /// (<c>gitlab:acme</c>) and it is compared against <c>Repository.OwnerKey</c>, which is computed
    /// from the repository's own <c>Provider</c> — so they can only match when the forge matches.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_legacy_token_for_another_forge_does_not_authorize_the_same_login_here()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session, OwnerId, "acme");
        WaitForIndexing(store);

        // Same login, different forge, and no numeric id — so the legacy arm is what runs.
        var gitlabToken = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Account"),
                new Claim(
                    ApiTokenAuthenticationHandler.AccountClaim,
                    new ForgeOwner(EForgeProvider.GitLab, "acme").ToString()),
            ],
            ApiTokenAuthenticationHandler.SchemeName));

        var controller = CreateController(session, gitlabToken);
        (await IsAuthorizedAsync(controller, "acme")).Should().BeFalse(
            "a token keyed to another forge must not authorize the same login here");
    }

    /// <summary>
    /// The paired positive, so the test above cannot pass against a fallback that authorizes
    /// nothing at all.
    /// </summary>
    [Fact]
    public async Task A_legacy_token_for_this_forge_still_authorizes()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session, OwnerId, "acme");
        WaitForIndexing(store);

        var controller = CreateController(session, AccountToken("acme", accountId: null));
        (await IsAuthorizedAsync(controller, "acme")).Should().BeTrue(
            "the legacy fallback still has to work, or the test above would pass against a fallback "
            + "that authorizes nothing at all");
    }

    [Fact]
    public async Task An_account_token_carrying_the_owner_id_authorizes_on_the_id()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session, OwnerId, "acme");
        WaitForIndexing(store);

        var controller = CreateController(session, AccountToken("acme", OwnerId));
        Assert.True(await IsAuthorizedAsync(controller, "acme"));
    }

    /// <summary>
    /// A token carrying an account id but no forge is refused, rather than assumed to be GitHub's.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A numeric account id is unique only WITHIN a forge.</b> GitHub user 1234 and a GitLab
    /// group 1234 are different principals, so a comparison that supplies the forge itself — as this
    /// did, as a literal, until 2026-09-22 — authorizes a token against whichever one the code
    /// assumed. Absent means refuse: defaulting is the bug, not the fallback.
    /// </remarks>
    [Fact]
    public async Task An_account_token_with_no_forge_is_refused_rather_than_assumed_to_be_GitHub()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session, OwnerId, "acme");
        WaitForIndexing(store);

        var withoutProvider = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Account"),
                new Claim(ApiTokenAuthenticationHandler.AccountClaim, ForgeOwner.KeyFromUnqualifiedLogin("acme")),
                new Claim(ApiTokenAuthenticationHandler.AccountIdClaim, OwnerId.ToString()),
            ],
            ApiTokenAuthenticationHandler.SchemeName));

        var controller = CreateController(session, withoutProvider);
        Assert.False(await IsAuthorizedAsync(controller, "acme"));
    }

    /// <summary>
    /// The transfer case. The token was issued by the previous owner, whose login it still carries;
    /// the repository now belongs to someone else. A login comparison would accept this.
    /// </summary>
    [Fact]
    public async Task A_previous_owners_token_is_refused_after_the_repository_moves()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session, OtherOwnerId, "acme");
        WaitForIndexing(store);

        // Token says account "acme" — which is still the repository's OwnerLogin — but names the
        // old owner's numeric id.
        var controller = CreateController(session, AccountToken("acme", OwnerId));
        Assert.False(await IsAuthorizedAsync(controller, "acme"));
    }

    /// <summary>
    /// Tokens issued before the id existed carry only the login, and must keep working — a deploy
    /// that silently invalidated every CI credential would be worse than the bug being fixed.
    /// </summary>
    [Fact]
    public async Task A_token_issued_before_the_id_existed_still_authorizes_on_the_login()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session, OwnerId, "acme");
        WaitForIndexing(store);

        var controller = CreateController(session, AccountToken("acme", accountId: null));
        Assert.True(await IsAuthorizedAsync(controller, "acme"));
    }

    /// <summary>
    /// A zero-file "partial" upload — legitimate when every project was cached or unaffected — is
    /// the lightest real upload there is, and it runs the same resolve-and-provision path.
    /// </summary>
    private static Task<ActionResult<UploadsController.UploadResponse>> UploadAsync(
        UploadsController controller, string fullName) =>
        controller.Upload(new UploadsController.UploadForm
        {
            Repository = fullName,
            CommitSha = "0123456789abcdef0123456789abcdef01234567",
            Branch = "master",
            RunId = 7,
            Partial = true,
            FileList = "src/a.cs",
        }, CancellationToken.None);

    /// <summary>
    /// D7. A workflow that still runs is proof the repository is alive and ours, and for one that
    /// moved to an owner where the App is not installed it is the only proof available.
    /// </summary>
    [Fact]
    public async Task An_OIDC_upload_reconnects_a_disconnected_repository()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session, OwnerId, "acme", RepositoryConnection.Disconnected);
        WaitForIndexing(store);

        var controller = CreateController(session, OidcToken("acme/widgets", RepoId));
        await UploadAsync(controller, "acme/widgets");

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        Assert.Equal(RepositoryConnection.Connected, repository!.Connection);
        Assert.Null(repository.DisconnectedReason);
    }

    /// <summary>
    /// The OIDC claims are GitHub-signed and current, so an upload from a repository we still know
    /// under its old name is also the freshest naming information available.
    /// </summary>
    [Fact]
    public async Task An_OIDC_upload_refreshes_a_stale_name_and_remembers_the_old_one()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session, OwnerId, "acme", RepositoryConnection.Disconnected);
        WaitForIndexing(store);

        var controller = CreateController(session, OidcToken("acme-renamed/widgets", RepoId));
        await UploadAsync(controller, "acme-renamed/widgets");

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        Assert.Equal("acme-renamed/widgets", repository!.FullName);
        Assert.Equal("acme-renamed", repository.OwnerLogin);
        Assert.Contains("acme/widgets", repository.PreviousFullNames);
    }

    /// <summary>
    /// The other half of "an upload reconnects": a <em>read</em> must not. Status resolves through
    /// the same code, and a GET saves nothing — so a reconnect there would appear to work and then
    /// vanish, which is worse than not doing it. Pins the `provision` gate.
    /// </summary>
    [Fact]
    public async Task Polling_status_does_not_reconnect_a_disconnected_repository()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedAsync(session, OwnerId, "acme", RepositoryConnection.Disconnected);
        WaitForIndexing(store);

        var controller = CreateController(session, OidcToken("acme/widgets", RepoId));
        await controller.Status("acme/widgets", "0123456789abcdef0123456789abcdef01234567", runId: 7);

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, RepoId));
        Assert.Equal(RepositoryConnection.Disconnected, repository!.Connection);
    }
}
