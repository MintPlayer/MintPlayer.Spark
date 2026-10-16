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
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IEnumerable<IForgeInstallationPermissions> permissionSources;

    /// <returns>
    /// 200 with the installation's page and the pending permissions, and with what the App itself does not declare of
    /// what the merge queue needs (§0 D95); 204 when neither has anything, or when
    /// nothing can be said (no installation, a forge without the concept, the forge unreachable); 404 for an
    /// account the viewer does not manage, exactly as for one that does not exist.
    /// </returns>
    [HttpGet]
    public async Task<ActionResult<PendingInstallationPermissions>> Get(string provider, string login, CancellationToken cancellationToken)
    {
        // The owner set first, so an account the viewer cannot manage takes the same steps as a missing one.
        // The MANAGED owners (R39, D-O12): only an organization's owner can accept the App's permissions anyway.
        var owners = await visibility.GetManagedOwnersAsync();
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
        // Dependency updates (dependency-updates PRD §7.2, D-N4): an account that turned them on is also told what the App
        // lacks for them (Workflows, Dependabot alerts, the release and dependabot_alert events). Their need is not in
        // the notice's default set, so an account without the feature is never asked for it.
        if (pending is not null && source is not null && account.DependencyUpdates
            && await source.GetAppGapsAsync(EMergeQueueAppNeed.DependencyUpdates, cancellationToken) is { IsEmpty: false } gaps)
            pending = pending with
            {
                MissingFromApp = [.. pending.MissingFromApp.Concat(gaps.MissingPermissions).DistinctBy(p => p.Name).OrderBy(p => p.Name, StringComparer.Ordinal)],
                MissingEvents = [.. pending.MissingEvents.Concat(gaps.MissingEvents).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                AppSettingsUrl = pending.AppSettingsUrl ?? gaps.SettingsUrl,
            };
        // §0 D95: what the App itself lacks is worth a notice too, though no one on the account can accept it.
        return pending is not null && (pending.Pending.Count > 0 || pending.MissingFromApp.Count > 0 || pending.MissingEvents.Count > 0)
            ? Ok(pending)
            : NoContent();
    }
}
