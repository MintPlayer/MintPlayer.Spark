using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Authorization;
using MintPlayer.Spark.Services;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Controllers;

[ApiController]
[Route("api/me")]
// Both, and [Authorize] is the load-bearing one. Read/Account is granted to the
// anonymous role as well (public repo pages read account documents), so
// [SparkAuthorize] alone would open "the accounts *I* administer" to callers who
// have no identity at all. The right is declared anyway so that revoking
// Read/Account from signed-in users closes this endpoint with it, rather than
// leaving an inconsistency between the controller and the generic UI.
[Authorize]
[SparkAuthorize("Read", nameof(Account))]
public partial class MeController : ControllerBase
{
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly IMyAccountsService myAccounts;

    /// <summary>
    /// The accounts (user + organizations) the signed-in user may see, joined
    /// with what we know about them (App installed or not) and an aggregate of
    /// their repositories' latest coverage. Carries the environment's GitHub
    /// App public page (GitHub:{env}:AppSlug, defaulting to the well-known
    /// per-environment slug) so "install the App" links point at the right App.
    /// </summary>
    [HttpGet("accounts")]
    public async Task<ActionResult<AccountsResponse>> GetAccounts(CancellationToken cancellationToken)
    {
        // The aggregation lives in MyAccountsService, shared with the
        // Custom.MyAccounts Spark query behind the composed Home page. The wire
        // shape stays this controller's own: AccountInfo's property order is what
        // the SPA deserializes, and reauth still travels as a flag on a 200 —
        // the auth interceptor hijacks any non-/spark/auth 401 into a full
        // /login navigation.
        var result = await myAccounts.GetAsync(cancellationToken);

        return Ok(new AccountsResponse(
            result.GitHubAppUrl,
            [.. result.Accounts.Select(a => new AccountInfo(
                a.Login, a.Type, a.AvatarUrl, a.IsAppInstalled, a.RepoCount, a.AggregateCoverage))],
            result.ReauthRequired));
    }

    /// <summary>
    /// Drops the cached GitHub visibility for the signed-in user and returns
    /// the freshly queried account list (manual counterpart of the 5-min TTL).
    /// </summary>
    [HttpPost("accounts/resync")]
    public async Task<ActionResult<AccountsResponse>> Resync(CancellationToken cancellationToken)
    {
        await gitHubAccess.InvalidateAsync(cancellationToken);
        return await GetAccounts(cancellationToken);
    }

    /// <param name="GitHubReauthRequired">The stored GitHub token is dead and silent refresh
    /// failed — only a browser round-trip (the "Reconnect GitHub" button) can fix it. While
    /// set, <paramref name="Accounts"/> is degraded to the user's own account.</param>
    public sealed record AccountsResponse(string GitHubAppUrl, AccountInfo[] Accounts, bool GitHubReauthRequired = false);
    public sealed record AccountInfo(string Login, string Type, string? AvatarUrl, bool Installed, int RepoCount, double? AggregateCoverage);
}
