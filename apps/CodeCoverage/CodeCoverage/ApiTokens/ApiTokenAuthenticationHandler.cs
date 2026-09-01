using System.Security.Claims;
using System.Text.Encodings.Web;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Raven.Client.Documents.Session;

namespace CodeCoverage.ApiTokens;

/// <summary>
/// Authenticates "Authorization: Bearer covt_…" (or "Token covt_…") by hashing
/// the presented value and point-loading ApiTokens/{hash}. Returns NoResult for
/// anything that doesn't look like our token so cookie/bearer schemes can try.
/// </summary>
public class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiToken";

    public const string ScopeClaim = "covt:scope";
    public const string AccountClaim = "covt:account";
    public const string RepositoryClaim = "covt:repoid";
    public const string TokenHashClaim = "covt:hash";

    private readonly IAsyncDocumentSession session;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAsyncDocumentSession session)
        : base(options, logger, encoder)
    {
        this.session = session;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
            return AuthenticateResult.NoResult();

        var parts = header.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0] is not ("Bearer" or "Token"))
            return AuthenticateResult.NoResult();

        var tokenValue = parts[1];
        if (!ApiTokenService.LooksLikeToken(tokenValue))
            return AuthenticateResult.NoResult();

        var hash = ApiTokenService.Hash(tokenValue);
        var token = await session.LoadAsync<ApiToken>(ApiToken.DocumentId(hash));
        if (token is null)
            return AuthenticateResult.Fail("Unknown token");
        if (token.RevokedAtUtc is not null)
            return AuthenticateResult.Fail("Token revoked");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, $"apitoken:{token.Description ?? hash[..8]}"),
            new(ScopeClaim, token.Scope),
            new(TokenHashClaim, hash),
        };
        if (token.AccountLogin is not null)
            claims.Add(new Claim(AccountClaim, token.AccountLogin));
        if (token.RepositoryGitHubId is not null)
            claims.Add(new Claim(RepositoryClaim, token.RepositoryGitHubId.Value.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
