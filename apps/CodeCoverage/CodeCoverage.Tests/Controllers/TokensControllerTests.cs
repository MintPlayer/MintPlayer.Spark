using System.Security.Claims;
using CodeCoverage.ApiTokens;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// Issuing and revoking upload tokens — the credentials that let CI write coverage.
/// <para>
/// It was at <b>0% coverage</b>. The riskiest property here is not that the happy path works but
/// that ownership is checked on <em>every</em> route: create, list AND revoke. Revoke is the one
/// most easily forgotten, because it takes a token id rather than an account name, so the
/// ownership check has to be derived from the loaded document instead of the request.
/// </para>
/// </summary>
public class TokensControllerTests : CoverageRavenTest
{
    private const string Owner = "acme";

    private static TokensController CreateController(
        IAsyncDocumentSession session,
        TestGitHubAccessService access,
        SparkUser? user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        services.AddSingleton<CodeCoverage.Services.IRepositoryResolver>(new TestRepositoryResolver(session));
        services.AddSingleton<CodeCoverage.Services.IGitHubAccessService>(access);
        services.AddSingleton<IUserStore<SparkUser>>(new StubUserStore(user));
        services.AddScoped<UserManager<SparkUser>, StubUserManager>();
        services.AddScoped<TokensController>();

        var controller = services.BuildServiceProvider().GetRequiredService<TokensController>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    user is null ? [] : [new Claim(ClaimTypes.NameIdentifier, user.Id!)],
                    user is null ? null : "test")),
            },
        };
        return controller;
    }

    /// <summary>Returns a fixed user (or none), which is all the controller asks of UserManager.</summary>
    private sealed class StubUserManager(IUserStore<SparkUser> store)
        : UserManager<SparkUser>(store, null!, null!, [], [], null!, null!, null!, null!)
    {
        public override Task<SparkUser?> GetUserAsync(ClaimsPrincipal principal)
            => Task.FromResult(((StubUserStore)store).User);
    }

    private sealed class StubUserStore(SparkUser? user) : IUserStore<SparkUser>
    {
        public SparkUser? User { get; } = user;
        public void Dispose() { }
        public Task<IdentityResult> CreateAsync(SparkUser u, CancellationToken c) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(SparkUser u, CancellationToken c) => throw new NotSupportedException();
        public Task<SparkUser?> FindByIdAsync(string id, CancellationToken c) => Task.FromResult(User);
        public Task<SparkUser?> FindByNameAsync(string name, CancellationToken c) => Task.FromResult(User);
        public Task<string?> GetNormalizedUserNameAsync(SparkUser u, CancellationToken c) => Task.FromResult<string?>(u.UserName);
        public Task<string> GetUserIdAsync(SparkUser u, CancellationToken c) => Task.FromResult(u.Id!);
        public Task<string?> GetUserNameAsync(SparkUser u, CancellationToken c) => Task.FromResult<string?>(u.UserName);
        public Task SetNormalizedUserNameAsync(SparkUser u, string? n, CancellationToken c) => Task.CompletedTask;
        public Task SetUserNameAsync(SparkUser u, string? n, CancellationToken c) => Task.CompletedTask;
        public Task<IdentityResult> UpdateAsync(SparkUser u, CancellationToken c) => throw new NotSupportedException();
    }

    private static SparkUser AUser() => new() { Id = "SparkUsers/1", UserName = "someone" };

    private static async Task SeedAccountAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = 42, Login = Owner }, Account.DocumentId(42));
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Creating_a_token_for_an_account_the_caller_does_not_manage_is_forbidden()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        // Degraded or simply foreign visibility: the caller manages nothing.
        var controller = CreateController(session, new TestGitHubAccessService(), AUser());

        var result = await controller.Create(new TokensController.CreateTokenRequest(Owner, "ci", null, null), default);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task A_created_token_returns_its_plaintext_once_and_stores_only_the_hash()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new TestGitHubAccessService(Owner), AUser());

        var result = await controller.Create(new TokensController.CreateTokenRequest(Owner, "ci", null, null), default);
        var created = Assert.IsType<TokensController.CreatedToken>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("Account", created.Scope);
        Assert.Equal(Owner, created.AccountLogin);

        // Only the HASH is stored. Storing the plaintext would make a database dump a set of
        // working upload credentials.
        var stored = await LoadByHashAsync(store, ApiTokenService.Hash(created.TokenValue));
        Assert.NotNull(stored);
        Assert.Equal(42, stored!.AccountGitHubId);

        // Nothing anywhere in the document may echo the plaintext.
        var json = System.Text.Json.JsonSerializer.Serialize(stored);
        Assert.DoesNotContain(created.TokenValue, json);
    }

    [Theory]
    [InlineData("Repository", null, typeof(BadRequestObjectResult))]         // scope needs a repo
    [InlineData("Repository", "not-a-full-name", typeof(BadRequestObjectResult))]
    [InlineData("Repository", "acme/unknown", typeof(NotFoundObjectResult))] // repo not here
    [InlineData("Nonsense", null, typeof(BadRequestObjectResult))]           // unknown scope
    public async Task Malformed_requests_are_rejected_before_a_token_is_minted(
        string scope, string? repositoryFullName, Type expected)
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new TestGitHubAccessService(Owner), AUser());

        var result = await controller.Create(
            new TokensController.CreateTokenRequest(Owner, "ci", scope, repositoryFullName), default);

        Assert.IsType(expected, result.Result);

        // And crucially: no credential was created on the way to rejecting the request.
        using var verify = store.OpenAsyncSession();
        var any = await verify.Query<ApiToken>().ToListAsync();
        Assert.Empty(any);
    }

    [Fact]
    public async Task Revoking_someone_elses_token_is_forbidden_and_leaves_it_usable()
    {
        using var store = GetDocumentStore();
        var value = ApiTokenService.GenerateTokenValue();
        var hash = ApiTokenService.Hash(value);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken
            {
                Scope = "Account",
                AccountLogin = Owner,
                CreatedAtUtc = DateTime.UtcNow,
                Hash = hash,
            }, ApiToken.NewDocumentId());
            await seed.SaveChangesAsync();
        }

        using var session = store.OpenAsyncSession();
        // Caller manages a DIFFERENT account. Revoke takes a hash, not an account name, so the
        // ownership check has to come from the loaded document -- the easiest one to leave out.
        var controller = CreateController(session, new TestGitHubAccessService("someone-else"), AUser());

        var result = await controller.Revoke(hash, default);

        Assert.IsType<ForbidResult>(result);

        var stored = await LoadByHashAsync(store, hash);
        Assert.Null(stored!.RevokedAtUtc);
    }

    [Fact]
    public async Task Revoking_an_owned_token_marks_it_revoked()
    {
        using var store = GetDocumentStore();
        var hash = ApiTokenService.Hash(ApiTokenService.GenerateTokenValue());

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken
            {
                Scope = "Account",
                AccountLogin = Owner,
                CreatedAtUtc = DateTime.UtcNow,
                Hash = hash,
            }, ApiToken.NewDocumentId());
            await seed.SaveChangesAsync();
        }

        using (var session = store.OpenAsyncSession())
        {
            var controller = CreateController(session, new TestGitHubAccessService(Owner), AUser());
            Assert.IsType<NoContentResult>(await controller.Revoke(hash, default));
        }

        Assert.NotNull((await LoadByHashAsync(store, hash))!.RevokedAtUtc);
    }

    [Fact]
    public async Task Revoking_an_unknown_token_is_a_404_not_a_403()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new TestGitHubAccessService(Owner), AUser());

        Assert.IsType<NotFoundResult>(await controller.Revoke("nosuchhash", default));
    }

    [Fact]
    public async Task Listing_tokens_for_an_unmanaged_account_is_forbidden()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new TestGitHubAccessService(), AUser());

        var result = await controller.List(Owner, default);

        Assert.IsType<ForbidResult>(result.Result);
    }

    /// <summary>
    /// Loads a token by its hash. The document id is a guid now, so the hash is a field lookup —
    /// see ApiToken's remarks for why the id stopped being the hash.
    /// </summary>
    private static async Task<ApiToken?> LoadByHashAsync(IDocumentStore store, string hash)
    {
        using var session = store.OpenAsyncSession();
        return await session.Query<ApiToken>()
            .Customize(q => q.WaitForNonStaleResults())
            .FirstOrDefaultAsync(t => t.Hash == hash);
    }
}
