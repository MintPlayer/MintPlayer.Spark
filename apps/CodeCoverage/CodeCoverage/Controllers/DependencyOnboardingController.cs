using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace CodeCoverage.Controllers;

/// <summary>
/// The onboarding page's list (dependency-updates PRD R20, R21, R32, R36): the repositories of the App's installations the
/// signed-in viewer manages, with their dependency-update state. The page passes the query its setup redirect arrived
/// with, untouched; which parameters mean what is the forge's business (<c>IForgeSetupRedirect</c>), so nothing here
/// names one.
/// </summary>
/// <remarks>
/// Signed-in only (<see cref="AuthorizeAttribute"/>), and only what the viewer manages: the forge lists the viewer's own
/// installations with the viewer's own credential, and an installation id the viewer cannot reach answers exactly like
/// one that does not exist. Enabling is the Spark custom action <c>EnableDependencyUpdates</c> on the query
/// <c>Repository_DependencyOnboarding</c>, which checks every row again.
/// </remarks>
[ApiController]
[Route("api/dependency-updates")]
[Authorize]
[SparkAuthorize("Read", nameof(Repository))]
public partial class DependencyOnboardingController : ControllerBase
{
    [Inject] private readonly IDependencyOnboardingService onboarding;

    [HttpGet("onboarding")]
    public async Task<ActionResult<DependencyOnboardingResponse>> GetOnboarding(CancellationToken cancellationToken)
    {
        // A repeated parameter arrives comma-joined (StringValues.ToString), never first-wins: a duplicated installation_id
        // is then malformed, and the forge's redirect reads it as an unknown installation (D-O13).
        var query = Request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.Ordinal);
        return Ok(await onboarding.ListAsync(query, cancellationToken));
    }
}
