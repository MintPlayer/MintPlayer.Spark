using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.CustomActions;

/// <summary>
/// An identity-provider administrator approves pending developer requests
/// (<c>docs/identity_provider_platform_PRD.md</c> D2). The selection is rebuilt by re-running
/// <c>Custom.DeveloperRequests</c>, so only users with a pending request can be approved this way.
/// </summary>
internal sealed partial class ApproveDeveloperAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly OidcDevelopers developers;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcPortalMail mail;
    [Inject] private readonly OidcPortalLinks links;
    [Inject] private readonly OidcPortalAccess access;

    // security.json grants the action to the administrators slot; checked here as well, as the go-live
    // decision is, so a misbound right cannot let a developer approve developers.
    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (!await access.IsAdministratorAsync()) return;
        await OidcDeveloperDecision.DecideAsync(
            args, approve: true, manager, session, httpContextAccessor, developers, audit, mail, links, cancellationToken);
    }
}

/// <summary>The rejecting counterpart of <see cref="ApproveDeveloperAction"/>.</summary>
internal sealed partial class RejectDeveloperAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly OidcDevelopers developers;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcPortalMail mail;
    [Inject] private readonly OidcPortalLinks links;
    [Inject] private readonly OidcPortalAccess access;

    // security.json grants the action to the administrators slot; checked here as well, as the go-live
    // decision is, so a misbound right cannot let a developer approve developers.
    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (!await access.IsAdministratorAsync()) return;
        await OidcDeveloperDecision.DecideAsync(
            args, approve: false, manager, session, httpContextAccessor, developers, audit, mail, links, cancellationToken);
    }
}

internal static class OidcDeveloperDecision
{
    public const string QueryAlias = "oidc-developer-requests";

    public static async Task DecideAsync(
        CustomActionArgs args,
        bool approve,
        IManager manager,
        IAsyncDocumentSession session,
        IHttpContextAccessor httpContextAccessor,
        OidcDevelopers developers,
        OidcAudit audit,
        OidcPortalMail mail,
        OidcPortalLinks links,
        CancellationToken ct)
    {
        var http = httpContextAccessor.HttpContext;
        var adminId = http?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var decided = new List<string>();

        foreach (var item in args.SelectedItems)
        {
            if (item.Id is not { Length: > 0 } userId)
                continue;

            // Re-read, strongly consistent: the row came from a query, and another administrator may
            // have decided this request since the list was drawn.
            var developer = await developers.GetAsync(userId, ct);
            if (developer is not { Status: OidcDeveloperStatuses.Requested })
                continue;

            developer.Status = approve ? OidcDeveloperStatuses.Approved : OidcDeveloperStatuses.Rejected;
            developer.DecidedAt = DateTime.UtcNow;
            developer.DecidedBy = adminId;
            OidcDevelopers.Set(session, userId, developer);
            await audit.RecordAsync(session,
                approve ? OidcAuditKinds.DeveloperApproved : OidcAuditKinds.DeveloperRejected,
                adminId, subjectId: userId, ipAddress: http?.Connection.RemoteIpAddress?.ToString(), ct: ct);
            decided.Add(userId);
        }

        if (decided.Count == 0)
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.developerRequestGone"), NotificationKind.Warning);
            return;
        }

        await session.SaveChangesAsync(ct);

        string? portalUrl = null;
        try { if (http is not null) portalUrl = links.Portal(http); }
        catch (InvalidOperationException) { /* no public base URL outside Development: the mail goes without the button */ }

        foreach (var userId in decided)
        {
            await mail.SendToUserAsync(userId,
                approve ? OidcPortalMailTemplates.DeveloperApproved : OidcPortalMailTemplates.DeveloperRejected,
                new { portal_url = portalUrl }, ct);
        }

        manager.Client.Notify(
            manager.GetTranslatedMessage(approve ? "identityProvider.developerApprovedNotice" : "identityProvider.developerRejectedNotice"),
            NotificationKind.Success);
        manager.Client.RefreshQuery(QueryAlias);
    }
}
