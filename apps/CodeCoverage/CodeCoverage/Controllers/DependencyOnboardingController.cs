using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace CodeCoverage.Controllers;

/// <summary>
/// The onboarding page's header (dependency-updates PRD R20, R36, D-O14): whether the viewer is linked, whether the sign-in
/// still works, whether the setup redirect narrowed the page, its hint, and how many repositories are still syncing (no
/// Repository document yet, so no row of the grid). The page passes the query its setup redirect arrived with, untouched;
/// which parameters mean what is the forge's business (<c>IForgeSetupRedirect</c>), so nothing here names one.
/// </summary>
/// <remarks>
/// The rows are not here: they are the Spark query <c>DependencyOnboardingRows</c> (virtual type <c>DependencyOnboardingRow</c>),
/// whose parent <c>DependencyOnboardingScope</c> carries the same query string and narrows through the same
/// <see cref="IDependencyOnboardingService.ListAsync"/>. Enabling is its query action <c>EnableDependencyUpdates</c>, which
/// checks every row again. Signed-in only, and only what the viewer manages: an installation id the viewer cannot reach
/// answers exactly like one that does not exist (D-O13).
/// </remarks>
[ApiController]
[Route("api/dependency-updates")]
[Authorize]
[SparkAuthorize("Read", nameof(Repository))]
public partial class DependencyOnboardingController : ControllerBase
{
    [Inject] private readonly IDependencyOnboardingService onboarding;

    [HttpGet("onboarding")]
    public async Task<ActionResult<DependencyOnboardingSummary>> GetOnboarding(CancellationToken cancellationToken)
    {
        // A repeated parameter arrives comma-joined (StringValues.ToString), never first-wins: a duplicated installation_id
        // is then malformed, and the forge's redirect reads it as an unknown installation (D-O13). DependencyOnboardingScope
        // reads the query's copy the same way.
        var query = Request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.Ordinal);
        return Ok(DependencyOnboardingSummary.From(await onboarding.ListAsync(query, cancellationToken)));
    }
}
