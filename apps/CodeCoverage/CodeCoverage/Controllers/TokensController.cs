using CodeCoverage.ApiTokens;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.Spark.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Controllers;

/// <summary>
/// Upload-token management for the signed-in user (cookie auth + XSRF via the
/// Spark middleware). Whether a user may manage an account's tokens mirrors
/// their GitHub visibility of that account.
/// </summary>
[ApiController]
[Route("api/tokens")]
// Manage/UploadToken is granted to the authenticated role only, so this replaces
// the bare [Authorize] rather than joining it. The per-account ownership checks
// stay in the method bodies: a declared right answers "may this caller manage
// upload tokens at all", never "does this caller own *this* account".
[SparkAuthorize("Manage", "UploadToken")]
public partial class TokensController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly UserManager<SparkUser> userManager;

    public sealed record CreateTokenRequest(string AccountLogin, string? Description, string? Scope, string? RepositoryFullName);
    public sealed record CreatedToken(string TokenValue, string AccountLogin, string? Description, string Scope, string? RepositoryFullName);
    public sealed record TokenInfo(string Id, string AccountLogin, string? Description, string Scope, string? RepositoryFullName,
        DateTime CreatedAtUtc, DateTime? RevokedAtUtc);

    [HttpPost]
    public async Task<ActionResult<CreatedToken>> Create([FromBody] CreateTokenRequest request, CancellationToken cancellationToken)
    {
        if (!await gitHubAccess.IsOwnerAllowedAsync(request.AccountLogin, cancellationToken))
            return Forbid();

        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        // A repo-scoped token still records AccountLogin so it shows up in the
        // account's token list; the upload handler authorizes on Scope alone.
        Repository? repository = null;
        if (request.Scope == "Repository")
        {
            if (string.IsNullOrWhiteSpace(request.RepositoryFullName))
                return BadRequest(new { error = "repositoryFullName is required for a repository-scoped token." });
            repository = await session.Query<Repository, Indexes.Repositories_Overview>()
                .Where(r => r.FullName == request.RepositoryFullName)
                .FirstOrDefaultAsync(cancellationToken);
            if (repository is null || !string.Equals(repository.OwnerLogin, request.AccountLogin, StringComparison.OrdinalIgnoreCase))
                return NotFound(new { error = $"Repository '{request.RepositoryFullName}' is unknown here or not owned by {request.AccountLogin}." });
        }
        else if (request.Scope is not (null or "Account"))
        {
            return BadRequest(new { error = "scope must be Account or Repository." });
        }

        var tokenValue = ApiTokenService.GenerateTokenValue();
        var token = new ApiToken
        {
            Scope = repository is null ? "Account" : "Repository",
            AccountLogin = request.AccountLogin,
            RepositoryGitHubId = repository?.GitHubId,
            Description = request.Description,
            CreatedByUserId = user.Id!,
            CreatedAtUtc = DateTime.UtcNow,
        };
        await session.StoreAsync(token, ApiToken.DocumentId(ApiTokenService.Hash(tokenValue)), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        // The plaintext value exists only in this response.
        return Ok(new CreatedToken(tokenValue, request.AccountLogin, request.Description, token.Scope, repository?.FullName));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TokenInfo>>> List([FromQuery] string account, CancellationToken cancellationToken)
    {
        if (!await gitHubAccess.IsOwnerAllowedAsync(account, cancellationToken))
            return Forbid();

        var tokens = await session.Query<ApiToken>()
            .Where(t => t.AccountLogin == account)
            .ToListAsync(cancellationToken);

        var repositories = await session.LoadAsync<Repository>(
            tokens.Where(t => t.RepositoryGitHubId is not null)
                  .Select(t => Repository.DocumentId(t.RepositoryGitHubId!.Value))
                  .Distinct(),
            cancellationToken);

        return Ok(tokens
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new TokenInfo(t.Id!, t.AccountLogin!, t.Description, t.Scope,
                t.RepositoryGitHubId is null ? null
                    : repositories.GetValueOrDefault(Repository.DocumentId(t.RepositoryGitHubId.Value))?.FullName,
                t.CreatedAtUtc, t.RevokedAtUtc)));
    }

    [HttpDelete("{hash}")]
    public async Task<IActionResult> Revoke(string hash, CancellationToken cancellationToken)
    {
        var token = await session.LoadAsync<ApiToken>(ApiToken.DocumentId(hash), cancellationToken);
        if (token is null) return NotFound();

        if (token.AccountLogin is null || !await gitHubAccess.IsOwnerAllowedAsync(token.AccountLogin, cancellationToken))
            return Forbid();

        token.RevokedAtUtc = DateTime.UtcNow;
        await session.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
