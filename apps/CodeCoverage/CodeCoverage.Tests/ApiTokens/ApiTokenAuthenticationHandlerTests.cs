using System.Security.Claims;
using System.Text.Encodings.Web;
using CodeCoverage.ApiTokens;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.ApiTokens;

/// <summary>
/// The handler that turns an upload token into an identity.
/// <para>
/// It was at <b>0% coverage</b> while being the sole authentication path for every CI upload this
/// service accepts. Nothing here is subtle, which is exactly why it deserves tests: the difference
/// between <c>NoResult</c> and <c>Fail</c> decides whether another scheme gets a turn, and the
/// difference between a revoked token failing and succeeding is the whole point of revocation.
/// </para>
/// </summary>
public class ApiTokenAuthenticationHandlerTests : CoverageRavenTest
{
    private static async Task<ApiTokenAuthenticationHandler> CreateAsync(
        IAsyncDocumentSession session, string? authorizationHeader)
    {
        var options = new Mock<AuthenticationSchemeOptions>();
        var handler = new ApiTokenAuthenticationHandler(
            options, NullLoggerFactory.Instance, UrlEncoder.Default, session);

        var context = new DefaultHttpContext();
        if (authorizationHeader is not null)
            context.Request.Headers.Authorization = authorizationHeader;

        await handler.InitializeAsync(
            new AuthenticationScheme(ApiTokenAuthenticationHandler.SchemeName, null, typeof(ApiTokenAuthenticationHandler)),
            context);

        return handler;
    }

    /// <summary>Minimal IOptionsMonitor; the handler only needs it to satisfy its base class.</summary>
    private sealed class Mock<T> : IOptionsMonitor<T> where T : class, new()
    {
        public T CurrentValue { get; } = new();
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static async Task<string> StoreTokenAsync(
        IAsyncDocumentSession session,
        Action<ApiToken>? customise = null)
    {
        var value = ApiTokenService.GenerateTokenValue();
        var token = new ApiToken
        {
            Scope = "Account",
            AccountLogin = "acme",
            AccountGitHubId = 42,
            CreatedAtUtc = DateTime.UtcNow,
            Description = "ci",
        };
        customise?.Invoke(token);

        token.Hash = ApiTokenService.Hash(value);
        await session.StoreAsync(token, ApiToken.NewDocumentId());
        await session.SaveChangesAsync();
        return value;
    }

    /// <summary>
    /// No header at all is NoResult, not Fail. The distinction matters: this scheme runs alongside
    /// the GitHub cookie scheme, and failing here would abort authentication for browser callers
    /// who never intended to present a token.
    /// </summary>
    [Theory]
    [InlineData(null)]           // no header
    [InlineData("")]             // empty header
    [InlineData("Basic abc")]    // wrong scheme
    [InlineData("Bearer")]       // scheme with no value
    [InlineData("Bearer not-a-token")] // right shape, not a token
    public async Task A_request_that_does_not_present_a_token_is_passed_over(string? header)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        var handler = await CreateAsync(session, header);
        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.Failure is not null, "an absent or foreign credential must be NoResult, not Fail");
    }

    [Fact]
    public async Task An_unknown_token_fails_rather_than_being_passed_over()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        // Well-formed but never issued: this IS a credential, and a wrong one, so it must Fail.
        var handler = await CreateAsync(session, $"Bearer {ApiTokenService.GenerateTokenValue()}");
        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task A_revoked_token_fails()
    {
        using var store = GetDocumentStore();
        using var seed = store.OpenAsyncSession();
        var value = await StoreTokenAsync(seed, t => t.RevokedAtUtc = DateTime.UtcNow);

        using var session = store.OpenAsyncSession();
        var handler = await CreateAsync(session, $"Bearer {value}");
        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
    }

    /// <summary>
    /// Both header spellings are accepted. The upload action sends "Token"; a hand-rolled curl
    /// almost always sends "Bearer". Dropping either would break real callers silently.
    /// </summary>
    [Theory]
    [InlineData("Bearer")]
    [InlineData("Token")]
    public async Task A_live_token_authenticates_and_carries_its_scope_and_owner(string scheme)
    {
        using var store = GetDocumentStore();
        using var seed = store.OpenAsyncSession();
        var value = await StoreTokenAsync(seed, t => t.GithubRepositories = [Repository.DocumentId(777)]);

        using var session = store.OpenAsyncSession();
        var handler = await CreateAsync(session, $"{scheme} {value}");
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded, result.Failure?.Message);

        var principal = result.Principal!;
        Assert.Equal("Account", principal.FindFirst(ApiTokenAuthenticationHandler.ScopeClaim)?.Value);
        Assert.Equal("acme", principal.FindFirst(ApiTokenAuthenticationHandler.AccountClaim)?.Value);
        Assert.Equal("42", principal.FindFirst(ApiTokenAuthenticationHandler.AccountIdClaim)?.Value);
        Assert.Equal("Repositories/777", principal.FindFirst(ApiTokenAuthenticationHandler.RepositoryClaim)?.Value);

        // The hash, never the token value: anything downstream that logs the principal must not be
        // able to leak a working credential.
        var hash = principal.FindFirst(ApiTokenAuthenticationHandler.TokenHashClaim)?.Value;
        Assert.NotNull(hash);
        Assert.DoesNotContain(value, hash);
        Assert.Equal(ApiTokenService.Hash(value), hash);
    }

    /// <summary>
    /// A token with no owner scoping still authenticates, and simply carries no owner claims.
    /// Absent must mean absent — an empty-string claim would read as "owned by nobody named ''"
    /// to any downstream check that only tests for presence.
    /// </summary>
    [Fact]
    public async Task Optional_owner_claims_are_omitted_rather_than_emitted_empty()
    {
        using var store = GetDocumentStore();
        using var seed = store.OpenAsyncSession();
        var value = await StoreTokenAsync(seed, t =>
        {
            t.AccountLogin = null;
            t.AccountGitHubId = null;
            t.GithubRepositories = [];
        });

        using var session = store.OpenAsyncSession();
        var handler = await CreateAsync(session, $"Bearer {value}");
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded, result.Failure?.Message);
        Assert.Null(result.Principal!.FindFirst(ApiTokenAuthenticationHandler.AccountClaim));
        Assert.Null(result.Principal!.FindFirst(ApiTokenAuthenticationHandler.AccountIdClaim));
        Assert.Null(result.Principal!.FindFirst(ApiTokenAuthenticationHandler.RepositoryClaim));
    }
}
