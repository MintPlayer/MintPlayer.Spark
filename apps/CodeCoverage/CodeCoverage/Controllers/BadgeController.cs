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
    [Inject] private readonly IConfiguration configuration;

    /// <summary>
    /// Default-branch coverage from the denormalized Repository.LatestCoverage;
    /// ?branch= and ?pr= read the newest covered commit of that branch or pull
    /// request instead.
    /// <para>
    /// ?pr= wins when both selectors are given — it is the more specific of the
    /// two, and erroring instead would leak that the combination was even
    /// understood for a repository the caller cannot see.
    /// </para>
    /// </summary>
    [HttpGet("badge/{owner}/{name}.svg")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Get(
        string owner, string name,
        [FromQuery] string? token, [FromQuery] string? branch, [FromQuery] int? pr, [FromQuery] string? sig,
        CancellationToken cancellationToken)
    {
        var repository = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.FullName == $"{owner}/{name}")
            .FirstOrDefaultAsync(cancellationToken);

        double? percent = null;
        var partial = false;
        if (repository is not null && MayView(repository, token, pr, sig))
        {
            CoverageSummary? summary;
            if (pr is not null)
                (summary, partial) = await LoadSelectorCoverage(repository, c => c.PullRequestNumber == pr, cancellationToken);
            else if (!string.IsNullOrEmpty(branch))
                (summary, partial) = await LoadSelectorCoverage(repository, c => c.Branch == branch, cancellationToken);
            else
                summary = repository.LatestCoverage;

            if (summary is { LinesCoverable: > 0 })
                percent = summary.LinesCovered * 100.0 / summary.LinesCoverable;
        }

        // The header depends only on the REQUEST (was a capability presented?),
        // never on whether the repo exists — a private-vs-public split keyed on
        // the repo would be the existence oracle the never-404 rule prevents.
        var presentedCapability = !string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(sig);
        Response.Headers.CacheControl = presentedCapability
            ? "private, max-age=300"
            : "public, max-age=300";

        var svg = BadgeRenderer.Coverage(percent, partial);

        // Weak ETag over the rendered bytes: as branch/PR variants multiply,
        // camo revalidates each distinct URL every 300s (measured), and a 304
        // is far cheaper than re-rendering against the 600/min per-IP window.
        var etag = $"W/\"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(svg)))[..16]}\"";
        Response.Headers.ETag = etag;
        if (Request.Headers.IfNoneMatch.Contains(etag))
            return StatusCode(StatusCodes.Status304NotModified);

        return Content(svg, "image/svg+xml; charset=utf-8");
    }

    /// <summary>
    /// Newest covered commit matching <paramref name="selector"/>, preferring a
    /// Complete assembly and falling back to any coverage flagged as partial.
    /// <para>
    /// The two-query shape is the fix for the asymmetry this endpoint shipped
    /// with: the repository badge is promoted only from a Complete assembly
    /// (CommitAssembler.Promote), so filtering merely on HasCoverage let
    /// ?branch= report a subset's total as if it were the whole, disagreeing
    /// with the unparameterised badge for the very same branch.
    /// </para>
    /// </summary>
    private async Task<(CoverageSummary? Summary, bool Partial)> LoadSelectorCoverage(
        Repository repository,
        System.Linq.Expressions.Expression<Func<Indexes.Commits_ByRepository.Result, bool>> selector,
        CancellationToken cancellationToken)
    {
        var complete = await QueryNewest(repository, selector, c => c.CompleteCoverage, cancellationToken);
        if (complete?.Coverage is not null) return (complete.Coverage, false);

        var any = await QueryNewest(repository, selector, c => c.HasCoverage, cancellationToken);
        return (any?.Coverage, any?.Coverage is not null);
    }

    private async Task<Commit?> QueryNewest(
        Repository repository,
        System.Linq.Expressions.Expression<Func<Indexes.Commits_ByRepository.Result, bool>> selector,
        System.Linq.Expressions.Expression<Func<Indexes.Commits_ByRepository.Result, bool>> coverage,
        CancellationToken cancellationToken)
        => await session.Query<Indexes.Commits_ByRepository.Result, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository == repository.Id)
            .Where(selector)
            .Where(coverage)
            .OrderByDescending(c => c.AuthoredAt)
            .OfType<Commit>()
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Public repos are always viewable. A private repo needs either the repo's
    /// badge token, or — for a ?pr= badge only — a signature scoped to exactly
    /// that (repository, pull request).
    /// <para>
    /// The signature exists so the bot can put a working badge in a PR comment
    /// without publishing BadgeToken, which is manager-only today and is a
    /// repo-wide credential with no expiry whose rotation breaks every README.
    /// </para>
    /// </summary>
    private bool MayView(Repository repository, string? token, int? pr, string? sig)
    {
        if (!repository.IsPrivate) return true;
        if (!string.IsNullOrEmpty(token) && FixedTimeEquals(repository.BadgeToken, token)) return true;
        if (pr is not null && !string.IsNullOrEmpty(sig))
            return BadgePrSignature.Verify(configuration, repository.GitHubId, pr.Value, sig);
        return false;
    }

    private static bool FixedTimeEquals(string? expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented)) return false;
        // Constant-time compare: the badge token is a credential, however narrow.
        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
