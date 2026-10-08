using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.IdentityProvider.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.CustomActions;

/// <summary>
/// Invites someone to an application's team (<c>docs/identity_provider_platform_PRD.md</c> D3), from
/// the application's page. Admins only. The answer is always "Invitation sent" (#453).
/// </summary>
internal sealed partial class InviteMemberAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcInvitations invitations;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcTeamMail teamMail;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } applicationId
            || await session.LoadAsync<OidcApplication>(applicationId, cancellationToken) is not { } app)
            return;

        if (!await access.IsApplicationAdminAsync(app))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.adminOnly"), NotificationKind.Error);
            return;
        }

        var prompt = await manager.GetPersistentObjectAsync("OidcInvitation", cancellationToken: cancellationToken);
        prompt["Role"].Value = OidcMemberRoles.Developer;
        var send = manager.GetTranslatedMessage("identityProvider.invite");
        manager.Retry.Action(
            title: manager.GetTranslatedMessage("identityProvider.inviteTitle"),
            options: [send],
            defaultOption: send,
            persistentObject: prompt,
            cancellable: true);

        var answer = manager.Retry.Result!;
        if (answer.Option == RetryResult.CancelOption || answer.PersistentObject is not { } form)
            return;

        var email = form.TryGetAttribute("Email", out var e) ? e.Value?.ToString()?.Trim() : null;
        var role = form.TryGetAttribute("Role", out var r) ? r.Value?.ToString() : null;
        if (string.IsNullOrEmpty(email) || !email.Contains('@') || role is not (OidcMemberRoles.Admin or OidcMemberRoles.Developer or OidcMemberRoles.Tester))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.inviteInvalid"), NotificationKind.Error);
            return;
        }

        var token = await invitations.InviteAsync(app, email, role, access.UserId!, cancellationToken);
        await audit.RecordAsync(session, OidcAuditKinds.MemberInvited, access.UserId, app.Id, ipAddress: access.IpAddress,
            details: new Dictionary<string, string> { ["role"] = role }, ct: cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        if (token is not null)
            await teamMail.SendInvitationAsync(app, email, role, token, cancellationToken);

        // The same words whether or not a mail went out.
        manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.invitationSent"), NotificationKind.Success);
        manager.Client.RefreshQuery(OidcApplicationMemberRowActions.QueryAlias);
    }
}

/// <summary>Sends a pending invitation again, with a new link and a new expiry. Admins only.</summary>
internal sealed partial class ResendInvitationAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcInvitations invitations;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcTeamMail teamMail;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (await OidcTeam.LoadAsync(args, session, cancellationToken) is not { } app) return;
        if (!await access.IsApplicationAdminAsync(app))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.adminOnly"), NotificationKind.Error);
            return;
        }

        var sends = new List<(string Email, string Role, string Token)>();
        foreach (var memberId in args.SelectedItems.Select(i => i.Id))
        {
            if (app.Members.FirstOrDefault(m => m.MemberId == memberId && m.Status == OidcMemberStatuses.Invited) is not { Email: { } email } pending)
                continue;
            if (await invitations.InviteAsync(app, email, pending.Role, access.UserId!, cancellationToken) is { } token)
                sends.Add((email, pending.Role, token));
            await audit.RecordAsync(session, OidcAuditKinds.MemberInvited, access.UserId, app.Id, ipAddress: access.IpAddress,
                details: new Dictionary<string, string> { ["role"] = pending.Role, ["resent"] = "true" }, ct: cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);
        foreach (var (email, role, token) in sends)
            await teamMail.SendInvitationAsync(app, email, role, token, cancellationToken);

        manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.invitationSent"), NotificationKind.Success);
        manager.Client.RefreshQuery(OidcApplicationMemberRowActions.QueryAlias);
    }
}

/// <summary>
/// Removes members or revokes pending invitations. Admins only, and never the last active Admin:
/// an application nobody can administer can only be rescued by an identity-provider administrator.
/// </summary>
internal sealed partial class RemoveMemberAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcAudit audit;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (await OidcTeam.LoadAsync(args, session, cancellationToken) is not { } app) return;
        if (!await access.IsApplicationAdminAsync(app))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.adminOnly"), NotificationKind.Error);
            return;
        }

        var ids = args.SelectedItems.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var removing = app.Members.Where(m => m.MemberId is not null && ids.Contains(m.MemberId) && m.Status != OidcMemberStatuses.Revoked).ToList();
        var adminsLeft = app.Members.Count(m => m.Status == OidcMemberStatuses.Active && m.Role == OidcMemberRoles.Admin && !removing.Contains(m));
        if (adminsLeft == 0)
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.lastAdmin"), NotificationKind.Error);
            return;
        }

        foreach (var member in removing)
        {
            member.Status = OidcMemberStatuses.Revoked;
            member.InvitationHash = null;
            await audit.RecordAsync(session, OidcAuditKinds.MemberRemoved, access.UserId, app.Id, member.UserId, access.IpAddress,
                new Dictionary<string, string> { ["role"] = member.Role }, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);
        manager.Client.RefreshQuery(OidcApplicationMemberRowActions.QueryAlias);
    }
}

/// <summary>
/// Switches an application to Live (D4). Admins only. With <c>Apps:RequireReviewToGoLive</c> the switch
/// becomes a request an identity-provider administrator decides; an administrator's own switch is
/// the decision.
/// </summary>
internal sealed partial class SwitchToLiveAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id || await session.LoadAsync<OidcApplication>(id, cancellationToken) is not { } app) return;
        if (!await access.IsApplicationAdminAsync(app))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.adminOnly"), NotificationKind.Error);
            return;
        }
        if (app.Mode == OidcApplicationModes.Live) return;

        if (options.Apps.RequireReviewToGoLive && !await access.IsAdministratorAsync())
        {
            app.GoLiveReview = new OidcReviewDecision { Status = OidcScopeStatuses.Pending, RequestedAt = DateTime.UtcNow, RequestedBy = access.UserId };
            await audit.RecordAsync(session, OidcAuditKinds.GoLiveRequested, access.UserId, app.Id, ipAddress: access.IpAddress, ct: cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.goLiveRequested"), NotificationKind.Info);
        }
        else
        {
            app.Mode = OidcApplicationModes.Live;
            app.GoLiveReview = null;
            await audit.RecordAsync(session, OidcAuditKinds.ApplicationModeSwitched, access.UserId, app.Id, ipAddress: access.IpAddress,
                details: new Dictionary<string, string> { ["mode"] = app.Mode }, ct: cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.nowLive"), NotificationKind.Success);
        }
        manager.Client.RefreshAttribute(args.Parent, nameof(OidcApplication.Mode));
    }
}

/// <summary>Takes an application back to Development: only its team can sign in again. Admins only.</summary>
internal sealed partial class SwitchToDevelopmentAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcAudit audit;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id || await session.LoadAsync<OidcApplication>(id, cancellationToken) is not { } app) return;
        if (!await access.IsApplicationAdminAsync(app))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("identityProvider.adminOnly"), NotificationKind.Error);
            return;
        }
        if (app.Mode == OidcApplicationModes.Development) return;

        app.Mode = OidcApplicationModes.Development;
        await audit.RecordAsync(session, OidcAuditKinds.ApplicationModeSwitched, access.UserId, app.Id, ipAddress: access.IpAddress,
            details: new Dictionary<string, string> { ["mode"] = app.Mode }, ct: cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        manager.Client.RefreshAttribute(args.Parent, nameof(OidcApplication.Mode));
    }
}

/// <summary>Loads the application a team row action was invoked under (the sub-query's container).</summary>
internal static class OidcTeam
{
    public static async Task<OidcApplication?> LoadAsync(CustomActionArgs args, IAsyncDocumentSession session, CancellationToken ct)
        => args.QueryParent?.Id is { Length: > 0 } id ? await session.LoadAsync<OidcApplication>(id, ct) : null;
}
