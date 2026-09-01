using CodeCoverage.Badges;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Controllers;

/// <summary>
/// README badges. Public repos render unauthenticated; private repos require
/// the repo's badge token — a capability that grants ONLY this SVG, never
/// report data. Wrong/missing token renders "unknown" (never 404: a 404 would
/// confirm the repo exists).
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting("badges")]
public partial class BadgeController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>
    /// Default-branch coverage from the denormalized Repository.LatestCoverage;
    /// ?branch= reads the newest covered commit of that branch instead.
    /// </summary>
    [HttpGet("badge/{owner}/{name}.svg")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Get(string owner, string name, [FromQuery] string? token, [FromQuery] string? branch, CancellationToken cancellationToken)
    {
        var repository = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.FullName == $"{owner}/{name}")
            .FirstOrDefaultAsync(cancellationToken);

        double? percent = null;
        if (repository is not null && MayView(repository, token))
        {
            var summary = string.IsNullOrEmpty(branch)
                ? repository.LatestCoverage
                : await LoadBranchCoverage(repository, branch, cancellationToken);
            if (summary is { LinesCoverable: > 0 })
                percent = summary.LinesCovered * 100.0 / summary.LinesCoverable;
        }

        // The header depends only on the REQUEST (was a capability presented?),
        // never on whether the repo exists — a private-vs-public split keyed on
        // the repo would be the existence oracle the never-404 rule prevents.
        Response.Headers.CacheControl = string.IsNullOrEmpty(token)
            ? "public, max-age=300"
            : "private, max-age=300";

        return Content(BadgeRenderer.Coverage(percent), "image/svg+xml; charset=utf-8");
    }

    private async Task<CoverageSummary?> LoadBranchCoverage(Repository repository, string branch, CancellationToken cancellationToken)
    {
        var commit = await session.Query<Indexes.Commits_ByRepository.Result, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository == repository.Id && c.Branch == branch && c.HasCoverage)
            .OrderByDescending(c => c.AuthoredAt)
            .OfType<Commit>()
            .FirstOrDefaultAsync(cancellationToken);
        return commit?.Coverage;
    }

    private static bool MayView(Repository repository, string? token)
    {
        if (!repository.IsPrivate) return true;
        if (string.IsNullOrEmpty(repository.BadgeToken) || string.IsNullOrEmpty(token)) return false;
        // Constant-time compare: the badge token is a credential, however narrow.
        var expected = System.Text.Encoding.UTF8.GetBytes(repository.BadgeToken);
        var presented = System.Text.Encoding.UTF8.GetBytes(token);
        return expected.Length == presented.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
