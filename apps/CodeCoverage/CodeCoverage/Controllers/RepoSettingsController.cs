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
    [Inject] private readonly IRepositoryResolver repositories;
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

    // The gate endpoints that lived here are gone. The policy is edited through the standard Spark
    // PO form on Repository now — the bespoke card that called them was deleted with them (#413) —
    // and their validation moved to RepositoryActions.OnBeforeSaveAsync, which is reached by every
    // writer rather than only by one hand-written page.

    private async Task<Repository?> ResolveOwnedRepository(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = (await repositories.ResolveAsync(owner, name, cancellationToken)).Repository;
        if (repository is null) return null;

        // NotFound for the unauthorized too, upstream of this: an existence
        // oracle is the thing the badge-token endpoint already refuses to be.
        return await gitHubAccess.IsOwnerAllowedAsync(repository.OwnerLogin, cancellationToken) ? repository : null;
    }
}
