using System.Security.Cryptography;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.Spark.Services;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Controllers;

[ApiController]
[Route("api/repos/{owner}/{name}/settings")]
// Authenticated-role-only right, replacing the bare [Authorize]. As with tokens,
// the per-repository ownership checks stay in the method bodies.
[SparkAuthorize("Manage", "RepoSettings")]
public partial class RepoSettingsController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubAccessService gitHubAccess;

    /// <summary>
    /// (Re)generates the badge token. Rotation invalidates the previous badge
    /// URL immediately; upload tokens are untouched.
    /// </summary>
    [HttpPost("badge-token")]
    public async Task<ActionResult<object>> RotateBadgeToken(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await ResolveOwnedRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        repository.BadgeToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await session.SaveChangesAsync(cancellationToken);

        return Ok(new { badgeToken = repository.BadgeToken });
    }

    /// <summary>The stored gate policy, defaults spelled out so the UI never guesses them.</summary>
    [HttpGet("gate")]
    public async Task<ActionResult<GateSettings>> GetGate(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await ResolveOwnedRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        return Ok(repository.Gate ?? new GateSettings());
    }

    [HttpPut("gate")]
    public async Task<ActionResult<GateSettings>> PutGate(string owner, string name, [FromBody] GateSettings gate, CancellationToken cancellationToken)
    {
        if (gate.ProjectMode is not ("auto" or "fixed"))
            return BadRequest(new { error = "projectMode must be auto or fixed." });
        if (gate.ProjectBasis is not ("scoped" or "projection"))
            return BadRequest(new { error = "projectBasis must be scoped or projection." });
        if (gate.ProjectTarget is < 0 or > 100 || gate.PatchTarget is < 0 or > 100)
            return BadRequest(new { error = "targets are percentages (0-100)." });
        if (gate.ProjectThreshold is < 0 or > 100 || gate.PatchThreshold is < 0 or > 100)
            return BadRequest(new { error = "thresholds are percentage points (0-100)." });
        if (gate.ProjectMode == "fixed" && gate.ProjectTarget is null)
            return BadRequest(new { error = "fixed mode needs a projectTarget." });

        var repository = await ResolveOwnedRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        repository.Gate = gate;
        await session.SaveChangesAsync(cancellationToken);
        return Ok(gate);
    }

    private async Task<Repository?> ResolveOwnedRepository(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.FullName == $"{owner}/{name}")
            .FirstOrDefaultAsync(cancellationToken);
        if (repository is null) return null;

        // NotFound for the unauthorized too, upstream of this: an existence
        // oracle is the thing the badge-token endpoint already refuses to be.
        return await gitHubAccess.IsOwnerAllowedAsync(repository.OwnerLogin, cancellationToken) ? repository : null;
    }
}
