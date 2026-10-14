using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.Spark.Services;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Controllers;

/// <summary>
/// The "accept the new permissions" notice on the Account page (merge-queue PRD O17): which permissions
/// the app now asks for that the account's installation has not accepted, and the forge's own page to
/// accept them on.
/// </summary>
[ApiController]
[Route("api/accounts/{provider}/{login}/pending-permissions")]
// The right the repository settings use: signed-in users only. The account check below is the one the
// settings use too — the viewer must manage the owner.
[SparkAuthorize("Manage", "RepoSettings")]
public partial class AccountPermissionsController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IEnumerable<IForgeInstallationPermissions> permissionSources;

    /// <returns>
    /// 200 with the installation's page and the pending permissions (possibly none); 204 when nothing can
    /// be said (no installation, a forge without the concept, the forge unreachable); 404 for an account
    /// the viewer does not manage, exactly as for one that does not exist.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<PendingInstallationPermissions>> Get(string provider, string login, CancellationToken cancellationToken)
    {
        // The owner set first, so an account the viewer cannot manage takes the same steps as a missing one.
        var owners = await forges.GetAllowedOwnerKeysAsync(cancellationToken);
        if (!ForgeProviders.TryParse(provider, out var forge))
            return NotFound();

        var ownerKey = new ForgeOwner(forge, login).ToString();
        if (!owners.Contains(ownerKey, StringComparer.OrdinalIgnoreCase))
            return NotFound();

        var account = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.OwnerKey == ownerKey)
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null)
            return NotFound();

        var source = permissionSources.FirstOrDefault(s => s.Provider == forge);
        var pending = source is null ? null : await source.GetPendingAsync(account, cancellationToken);
        return pending is null ? NoContent() : Ok(pending);
    }
}
