using CodeCoverage.Forge;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CodeCoverage.Entities;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
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
    /// <summary>The owner's numeric forge id — the stable half of <see cref="AccountClaim"/>.</summary>
    public const string AccountIdClaim = "covt:accountid";
    /// <summary>The forge the token authorizes against.</summary>
    /// <remarks>
    /// ⚠️ <b><see cref="AccountIdClaim"/> means nothing without it.</b> A numeric account id is
    /// unique only within a forge, so a reader that assumes one — as the upload path did, as a
    /// literal — authorizes against whichever forge it happened to pick.
    /// </remarks>
    public const string ProviderClaim = "covt:provider";
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

        // ⚠️ A query, where this used to be a point-load on ApiTokens/{hash}. The document id is a
        // guid now, so that the hash is not part of the id a PersistentObject puts on the wire —
        // see ApiToken's remarks. The trade is deliberate: never sending the hash beats sending it
        // only to the right callers, which depends on a row filter staying correct.
        //
        // Exactly one document can match: Hash is derived from 32 bytes of RandomNumberGenerator,
        // and the write path is the only thing that sets it.
        var token = await session.Query<ApiToken>()
            .Where(t => t.Hash == hash)
            .FirstOrDefaultAsync();

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
        // ⚠️ The KEY (`github:acme`), not the bare login. This is the fallback for tokens minted
        // before `AccountId` existed, and a bare login is ambiguous the moment there is a second
        // forge: a GitLab group and a GitHub organisation of the same name would authorize each
        // other's uploads. `AccountOwnerKey` is server-derived on every save and read-only in the
        // model, so unlike the login it was never a value the client could choose.
        if (token.AccountOwnerKey is not null)
            claims.Add(new Claim(AccountClaim, token.AccountOwnerKey));
        if (token.AccountId is not null)
        {
            claims.Add(new Claim(AccountIdClaim, token.AccountId.Value.ToString()));
            // Emitted with the id, never without it: the pair is what identifies an account, and a
            // consumer holding one half would have to guess the other.
            claims.Add(new Claim(ProviderClaim, token.Provider.ToCanonicalString()));
        }
        // One claim per repository. ClaimsIdentity carries repeats happily, but a reader MUST use
        // FindAll — FindFirst silently returns one of N, which would authorize a multi-repository
        // token for exactly one repository and fail closed on the rest, confusingly.
        // The value is the DOCUMENT id, matching what is stored, so nothing has to re-derive it.
        foreach (var repositoryId in token.RepositoryIds)
            claims.Add(new Claim(RepositoryClaim, repositoryId));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
