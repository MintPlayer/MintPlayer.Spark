using CodeCoverage.Forge;
using System.Security.Cryptography;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.Spark.Services;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Controllers;

[ApiController]
[Route("api/repos/{provider}/{owner}/{name}/settings")]
// Authenticated-role-only right, replacing the bare [Authorize]. As with tokens,
// the per-repository ownership checks stay in the method bodies.
[SparkAuthorize("Manage", "RepoSettings")]
public partial class RepoSettingsController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IRepositoryResolver repositories;
    [Inject] private readonly IForgeIntegrationResolver forges;

    /// <summary>
    /// (Re)generates the badge token. Rotation invalidates the previous badge
    /// URL immediately; upload tokens are untouched.
    /// </summary>
    // ⚠️ RequireAntiforgeryToken, not MVC's [ValidateAntiForgeryToken]. Spark's gate reads
    // IAntiforgeryMetadata; MVC's attribute implements a different interface the gate never sees, so
    // it compiles, reads as protection, and does nothing (#300).
    //
    // Explicit rather than relying on the path-prefix default: that default is inert here, because
    // the app sets PathPrefixes and WarnOnly but never RequireAntiforgery. Until it does, an
    // unstamped cookie-authenticated POST is checked by nothing — and this one rotates a token that
    // breaks every published badge URL, which is worth more than a configuration flag's reach.
    [HttpPost("badge-token")]
    [RequireAntiforgeryToken]
    public async Task<ActionResult<object>> RotateBadgeToken(string provider, string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await ResolveOwnedRepository(provider, owner, name, cancellationToken);
        if (repository is null) return NotFound();

        repository.BadgeToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await session.SaveChangesAsync(cancellationToken);

        return Ok(new { badgeToken = repository.BadgeToken });
    }

    // The gate endpoints that lived here are gone. The policy is edited through the standard Spark
    // PO form on Repository now — the bespoke card that called them was deleted with them (#413) —
    // and their validation moved to RepositoryActions.OnBeforeSaveAsync, which is reached by every
    // writer rather than only by one hand-written page.

    private async Task<Repository?> ResolveOwnedRepository(
        string provider, string owner, string name, CancellationToken cancellationToken)
    {
        // An unrecognised forge is not ours to answer for; same result as an unknown repository.
        if (!ForgeProviders.TryParse(provider, out var parsedProvider))
            return null;

        var repository = (await repositories.ResolveAsync(parsedProvider, owner, name, cancellationToken)).Repository;
        if (repository is null) return null;

        // NotFound for the unauthorized too, upstream of this: an existence
        // oracle is the thing the badge-token endpoint already refuses to be.
        var forge = forges.For(repository);
        return await forge.IsOwnerAllowedAsync(new ForgeOwner(forge.Provider, repository.OwnerLogin), cancellationToken) ? repository : null;
    }
}
