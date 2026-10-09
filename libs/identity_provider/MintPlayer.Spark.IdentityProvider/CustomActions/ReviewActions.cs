using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.IdentityProvider.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.CustomActions;

/// <summary>An API owner approves applications' pending requests for its scopes (D4).</summary>
internal sealed partial class ApproveScopeAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcPortalMail mail;

    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => OidcScopeDecision.DecideAsync(args, approve: true, manager, session, access, audit, mail, cancellationToken);
}

/// <summary>An API owner rejects applications' pending requests for its scopes (D4).</summary>
internal sealed partial class RejectScopeAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcPortalMail mail;

    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => OidcScopeDecision.DecideAsync(args, approve: false, manager, session, access, audit, mail, cancellationToken);
}

internal static class OidcScopeDecision
{
    public static async Task DecideAsync(CustomActionArgs args, bool approve, IManager manager, IAsyncDocumentSession session,
        OidcPortalAccess access, OidcAudit audit, OidcPortalMail mail, CancellationToken ct)
    {
        var notify = new List<(OidcApplication App, string Scope, string Resource)>();
        foreach (var id in args.SelectedItems.Select(i => i.Id))
        {
            // The row came from the query, which already checked ownership; it is checked again on the
            // documents, because the selection is ids and the decision is made on the server's state.
            var separator = id.LastIndexOf('|');
            if (separator <= 0) continue;
            var app = await session.LoadAsync<OidcApplication>(id[..separator], ct);
            var scopeName = id[(separator + 1)..];
            var scope = app?.Scopes.FirstOrDefault(s => s.Name == scopeName && s.Status == OidcScopeStatuses.Pending);
            if (app is null || scope is null || OidcScopeCatalog.ApiResourceNameOf(scopeName) is not { } apiName) continue;
            var api = await session.LoadAsync<OidcResource>(OidcScopeCatalog.ResourceId(apiName), ct);
            if (api is null || !await access.OwnsAsync(api)) continue;

            scope.Status = approve ? OidcScopeStatuses.Approved : OidcScopeStatuses.Rejected;
            scope.Review ??= new OidcReviewDecision();
            scope.Review.Status = scope.Status;
            scope.Review.DecidedAt = DateTime.UtcNow;
            scope.Review.DecidedBy = access.UserId;
            await audit.RecordAsync(session, OidcAuditKinds.ScopeApprovalDecided, access.UserId, app.Id, ipAddress: access.IpAddress,
                details: new Dictionary<string, string> { ["scope"] = scopeName, ["decision"] = scope.Status }, ct: ct);
            notify.Add((app, scopeName, api.Name));
        }

        if (notify.Count == 0) return;
        await session.SaveChangesAsync(ct);

        foreach (var (app, scope, resource) in notify)
            foreach (var admin in app.Members.Where(m => m.Status == OidcMemberStatuses.Active && m.Role == OidcMemberRoles.Admin && m.UserId is not null))
                await mail.SendToUserAsync(admin.UserId!, OidcPortalMailTemplates.ScopeApprovalDecided,
                    new { approved = approve, scope, resource, application = app.DisplayName }, ct);

        manager.Client.RefreshQuery(OidcScopeApprovalActions.QueryAlias);
    }
}

/// <summary>An identity-provider administrator lets applications go Live (D4).</summary>
internal sealed partial class ApproveGoLiveAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcPortalMail mail;

    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => OidcGoLiveDecision.DecideAsync(args, approve: true, manager, session, access, audit, mail, cancellationToken);
}

/// <summary>An identity-provider administrator keeps applications in Development (D4).</summary>
internal sealed partial class RejectGoLiveAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcPortalMail mail;

    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => OidcGoLiveDecision.DecideAsync(args, approve: false, manager, session, access, audit, mail, cancellationToken);
}

internal static class OidcGoLiveDecision
{
    public static async Task DecideAsync(CustomActionArgs args, bool approve, IManager manager, IAsyncDocumentSession session,
        OidcPortalAccess access, OidcAudit audit, OidcPortalMail mail, CancellationToken ct)
    {
        if (!await access.IsAdministratorAsync()) return;

        var decided = new List<OidcApplication>();
        foreach (var id in args.SelectedItems.Select(i => i.Id))
        {
            var app = await session.LoadAsync<OidcApplication>(id, ct);
            if (app?.GoLiveReview is not { Status: OidcScopeStatuses.Pending } review) continue;

            review.Status = approve ? OidcScopeStatuses.Approved : OidcScopeStatuses.Rejected;
            review.DecidedAt = DateTime.UtcNow;
            review.DecidedBy = access.UserId;
            if (approve) app.Mode = OidcApplicationModes.Live;
            await audit.RecordAsync(session, OidcAuditKinds.GoLiveDecided, access.UserId, app.Id, ipAddress: access.IpAddress,
                details: new Dictionary<string, string> { ["decision"] = review.Status }, ct: ct);
            decided.Add(app);
        }

        if (decided.Count == 0) return;
        await session.SaveChangesAsync(ct);

        foreach (var app in decided)
            foreach (var admin in app.Members.Where(m => m.Status == OidcMemberStatuses.Active && m.Role == OidcMemberRoles.Admin && m.UserId is not null))
                await mail.SendToUserAsync(admin.UserId!, OidcPortalMailTemplates.GoLiveDecided,
                    new { approved = approve, application = app.DisplayName }, ct);

        manager.Client.RefreshQuery(OidcGoLiveReviewActions.QueryAlias);
    }
}
