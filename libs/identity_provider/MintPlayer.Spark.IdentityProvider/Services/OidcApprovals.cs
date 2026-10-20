using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// A user asks to become a developer (<c>docs/identity_provider_platform_PRD.md</c> D2; commerce D26). Raised by
/// <c>POST /spark/identity-provider/developer</c> under <c>Developers:RequireApproval</c>; decided by an
/// identity-provider administrator. The effective status stays on the user document (<see cref="OidcDeveloper"/>),
/// which group membership point-loads on every request; the request is the queue, the decision, the reason and the history.
/// </summary>
public sealed class OidcDeveloperApprovalKind : IApprovalKind
{
    public static string Name => "identity-provider.developer";
}

/// <summary>
/// An application asks for an API scope whose API nobody on its team owns (D4). Raised for each such scope when it
/// is added (the PO save, RFC 7591 registration, RFC 7592 update); the subject is the application and the scope
/// (<see cref="OidcApprovalRequests.ScopeSubject"/>). Decided by an owner of that API or an administrator. The scope row
/// is <c>Pending</c> meanwhile: available to the team only (<see cref="OidcApplicationAccess.AvailableScopes"/>).
/// </summary>
public sealed class OidcScopeApprovalKind : IApprovalKind
{
    public static string Name => "identity-provider.scope";
}

/// <summary>
/// An application's Admin asks to switch it to Live under <c>Apps:RequireReviewToGoLive</c> (D4). Decided by an
/// administrator; approving sets <see cref="OidcApplication.Mode"/> to Live.
/// </summary>
public sealed class OidcGoLiveApprovalKind : IApprovalKind
{
    public static string Name => "identity-provider.go-live";
}

/// <summary>The subjects and submissions of the identity provider's three approval kinds.</summary>
internal static class OidcApprovalRequests
{
    /// <summary><see cref="ApprovalSubject.Type"/> of a developer request: the subject is the user (its document id).</summary>
    public const string DeveloperSubjectType = "OidcDeveloper";

    /// <summary><see cref="ApprovalSubject.Type"/> of a scope or go-live request.</summary>
    public const string ApplicationSubjectType = nameof(OidcApplication);

    public static ApprovalSubject DeveloperSubject(string userId) => new(DeveloperSubjectType, userId);

    /// <summary>One scope of one application: an application asking for several scopes raises one request per scope.</summary>
    public static ApprovalSubject ScopeSubject(string applicationId, string scope) => new(ApplicationSubjectType, applicationId, scope);

    /// <summary>The application itself: a go-live request; with no key it also matches every scope request of the application.</summary>
    public static ApprovalSubject ApplicationSubject(string applicationId) => new(ApplicationSubjectType, applicationId);

    /// <summary>The application's page, app-relative (the PO route by type id: the type has no alias).</summary>
    public static string ApplicationUrl(string applicationId)
        => $"/po/{IdentityProviderTypeIds.OidcApplication}/{Uri.EscapeDataString(applicationId)}";

    /// <summary>The team that may follow a request about the application: its active Admins and Developers.</summary>
    public static List<string> Team(OidcApplication app)
        => app.Members
            .Where(m => m.Status == OidcMemberStatuses.Active && m.Role is OidcMemberRoles.Admin or OidcMemberRoles.Developer && m.UserId is { Length: > 0 })
            .Select(m => m.UserId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string NameOf(OidcApplication app) => string.IsNullOrWhiteSpace(app.DisplayName) ? app.ClientId : app.DisplayName;

    public static ApprovalSubmission Developer(string userId, string? userName, string? email, int termsVersion) => new()
    {
        Subject = DeveloperSubject(userId),
        Title = $"{userName ?? email ?? userId} asks for developer access",
        Summary = string.IsNullOrEmpty(email)
            ? $"{userName ?? userId} accepted the developer terms (version {termsVersion}) and asks for developer access."
            : $"{userName ?? email} ({email}) accepted the developer terms (version {termsVersion}) and asks for developer access.",
        RequestedBy = userId,
    };

    public static ApprovalSubmission Scope(OidcApplication app, string scope, string apiName, string requestedBy) => new()
    {
        Subject = ScopeSubject(app.Id!, scope),
        Title = $"{NameOf(app)} asks for {scope}",
        Summary = $"The application {NameOf(app)} ({app.ClientId}) asks for the scope {scope} of the API {apiName}, which nobody on its team owns. "
                + "Until it is approved, only the application's own team can use it.",
        Url = ApplicationUrl(app.Id!),
        RequestedBy = requestedBy,
        VisibleTo = Team(app),
    };

    public static ApprovalSubmission GoLive(OidcApplication app, string requestedBy) => new()
    {
        Subject = ApplicationSubject(app.Id!),
        Title = $"{NameOf(app)} asks to go Live",
        Summary = $"The application {NameOf(app)} ({app.ClientId}) asks to switch to Live: anyone could then sign in to it, not only its team.",
        Url = ApplicationUrl(app.Id!),
        RequestedBy = requestedBy,
        VisibleTo = Team(app),
    };

    /// <summary>
    /// D4: marks each of <paramref name="added"/> whose API the application's team does not own <c>Pending</c>, and
    /// returns those with their API's name. Identity scopes, the team's own APIs and <c>AutoApprove</c> APIs are left
    /// as they are (approved).
    /// </summary>
    public static async Task<List<(OidcApplicationScope Scope, string ApiName)>> MarkCrossOwnerScopesAsync(
        IAsyncDocumentSession session, OidcApplication app, IEnumerable<OidcApplicationScope> added, CancellationToken ct = default)
    {
        var team = app.Members.Where(m => m.Status == OidcMemberStatuses.Active && m.UserId is not null)
            .Select(m => m.UserId!).ToHashSet(StringComparer.Ordinal);
        var marked = new List<(OidcApplicationScope, string)>();
        foreach (var scope in added)
        {
            if (OidcScopeCatalog.ApiResourceNameOf(scope.Name) is not { } apiName)
                continue; // identity scope
            var api = await session.LoadAsync<OidcResource>(OidcScopeCatalog.ResourceId(apiName), ct);
            if (api is null || api.Kind != OidcResourceKinds.Api || api.AutoApprove || api.Owners.Any(team.Contains))
                continue;
            scope.Status = OidcScopeStatuses.Pending;
            marked.Add((scope, api.Name));
        }
        return marked;
    }

    /// <summary>
    /// Marks the cross-owner scopes among <paramref name="added"/> <c>Pending</c> and raises one
    /// <see cref="OidcScopeApprovalKind"/> request for each, inside <paramref name="session"/>: the requests commit with
    /// the application or not at all. The requester is named (a registration call has no signed-in user).
    /// </summary>
    public static async Task RaiseScopeRequestsAsync(ISparkApprovals approvals, IAsyncDocumentSession session, OidcApplication app,
        IEnumerable<OidcApplicationScope> added, string? requestedBy, CancellationToken ct = default)
    {
        var marked = await MarkCrossOwnerScopesAsync(session, app, added, ct);
        // Nobody to name as the requester (no user, no creator): the scope stays Pending, decided by nobody, served
        // to the team only. Unreachable from the three creation paths, which all know who asks.
        if (marked.Count == 0 || (requestedBy ?? app.CreatedBy) is not { Length: > 0 } requester)
            return;
        foreach (var (scope, apiName) in marked)
            await approvals.SubmitAsync<OidcScopeApprovalKind>(session, Scope(app, scope.Name, apiName, requester), ct);
    }

    /// <summary>Withdraws the pending requests for scopes the application no longer has (inside the caller's session).</summary>
    public static async Task ClosePendingScopeRequestsAsync(ISparkApprovals approvals, IAsyncDocumentSession session, string applicationId,
        IEnumerable<OidcApplicationScope> removed, CancellationToken ct = default)
    {
        foreach (var scope in removed.Where(s => s.Status == OidcScopeStatuses.Pending))
            await approvals.ClosePendingAsync<OidcScopeApprovalKind>(session, ScopeSubject(applicationId, scope.Name), ct);
    }
}

/// <summary>Who decides a developer request: an identity-provider administrator (<c>ManageAll/IdentityProvider</c>), live.</summary>
internal sealed partial class OidcDeveloperApprovalDeciders : IApprovalDeciders<OidcDeveloperApprovalKind>
{
    [Inject] private readonly OidcPortalAccess access;

    public async ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context) => await access.IsAdministratorAsync();

    // The administrators are the holders of a right, which nothing lists: they see every developer request instead.
    public async ValueTask<ApprovalScope> DecidesAllAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => await access.IsAdministratorAsync() ? ApprovalScope.All : ApprovalScope.None;
}

/// <summary>
/// What deciding a developer request does to the status on the user's document, written with a patch deferred into
/// the decision's session (it commits with the request's new status). Approve and reject apply only to a status still
/// <c>Requested</c>; revoking only to an <c>Approved</c> one; anything else makes the request stale. A rejection's
/// and a revocation's reason is kept as <see cref="OidcDeveloper.Note"/>, which the portal shows (F3).
/// </summary>
internal sealed partial class OidcDeveloperApprovalHandler : IApprovalHandler<OidcDeveloperApprovalKind>
{
    [Inject] private readonly OidcDevelopers developers;
    [Inject] private readonly OidcAudit audit;

    public Task ApproveAsync(ApprovalDecisionContext context)
        => DecideAsync(context, OidcDeveloperStatuses.Requested, OidcDeveloperStatuses.Approved, OidcAuditKinds.DeveloperApproved);

    public Task RejectAsync(ApprovalDecisionContext context)
        => DecideAsync(context, OidcDeveloperStatuses.Requested, OidcDeveloperStatuses.Rejected, OidcAuditKinds.DeveloperRejected);

    public Task RevokeAsync(ApprovalDecisionContext context)
        => DecideAsync(context, OidcDeveloperStatuses.Approved, OidcDeveloperStatuses.Revoked, OidcAuditKinds.DeveloperRevoked);

    /// <summary>The user took the request back, or nobody decided in time: they are no developer and may ask again.</summary>
    public Task WithdrawAsync(ApprovalDecisionContext context) => ClearAsync(context);

    public Task ExpireAsync(ApprovalDecisionContext context) => ClearAsync(context);

    private async Task DecideAsync(ApprovalDecisionContext context, string from, string to, string auditKind)
    {
        var userId = context.Request.Subject.Id;
        // Strongly consistent, in a session of its own (OidcDevelopers): the user document may be tracked as the
        // application's user type elsewhere.
        var developer = await developers.GetAsync(userId, context.CancellationToken);
        if (developer is null || developer.Status != from)
        {
            context.MarkStale();
            return;
        }

        developer.Status = to;
        developer.DecidedAt = DateTime.UtcNow;
        developer.DecidedBy = context.DecidedBy;
        developer.Note = to == OidcDeveloperStatuses.Approved ? null : context.Reason;
        OidcDevelopers.Set(context.Session, userId, developer);
        await audit.RecordAsync(context.Session, auditKind, context.DecidedBy, subjectId: userId,
            details: context.Reason is { } reason ? new Dictionary<string, string> { ["reason"] = reason } : null,
            ct: context.CancellationToken);
    }

    private async Task ClearAsync(ApprovalDecisionContext context)
    {
        var userId = context.Request.Subject.Id;
        if (await developers.GetAsync(userId, context.CancellationToken) is { Status: OidcDeveloperStatuses.Requested })
            OidcDevelopers.Clear(context.Session, userId);
    }
}

/// <summary>
/// Who decides a cross-owner scope request: an owner of the API the scope belongs to (<see cref="OidcResource.Owners"/>,
/// read at decision time, so an owner removed since cannot decide), or an administrator. The owners are listed, so each
/// is told and sees the request in their inbox; administrators see every one.
/// </summary>
internal sealed partial class OidcScopeApprovalDeciders : IApprovalDeciders<OidcScopeApprovalKind>
{
    [Inject] private readonly OidcPortalAccess access;

    public async ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context)
    {
        if (await ApiOfAsync(context.Request.Subject, context.Session, context.CancellationToken) is { } api && api.Owners.Contains(context.UserId))
            return true;
        return await access.IsAdministratorAsync();
    }

    public async ValueTask<IReadOnlyCollection<string>> GetDeciderIdsAsync(ApprovalSubject subject, IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        if (await ApiOfAsync(subject, session, cancellationToken) is not { } api)
            return [];
        return api.Owners.ToList();
    }

    public async ValueTask<ApprovalScope> DecidesAllAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => await access.IsAdministratorAsync() ? ApprovalScope.All : ApprovalScope.None;

    /// <summary>The API resource the subject's scope belongs to, by its prefix (<c>fleet.read</c> → <c>fleet</c>).</summary>
    private static async Task<OidcResource?> ApiOfAsync(ApprovalSubject subject, IAsyncDocumentSession session, CancellationToken ct)
    {
        if (subject.Key is not { Length: > 0 } scope || OidcScopeCatalog.ApiResourceNameOf(scope) is not { } apiName)
            return null;
        var api = await session.LoadAsync<OidcResource>(OidcScopeCatalog.ResourceId(apiName), ct);
        return api is { Kind: OidcResourceKinds.Api } ? api : null;
    }
}

/// <summary>
/// What deciding a scope request does: it sets that one scope row's <see cref="OidcApplicationScope.Status"/>, nothing
/// else of the application (whose change vector moves on its own, research §9.3.2). The reason stays on the request.
/// A row that is gone or no longer <c>Pending</c> makes the request stale. Withdrawn or expired, the ask is dropped
/// with the request: the still-pending row is removed, and the team adds the scope again to ask again.
/// </summary>
internal sealed partial class OidcScopeApprovalHandler : IApprovalHandler<OidcScopeApprovalKind>
{
    [Inject] private readonly OidcAudit audit;

    public Task ApproveAsync(ApprovalDecisionContext context) => DecideAsync(context, OidcScopeStatuses.Approved);

    public Task RejectAsync(ApprovalDecisionContext context) => DecideAsync(context, OidcScopeStatuses.Rejected);

    public Task WithdrawAsync(ApprovalDecisionContext context) => DropAsync(context);

    public Task ExpireAsync(ApprovalDecisionContext context) => DropAsync(context);

    private async Task DecideAsync(ApprovalDecisionContext context, string status)
    {
        var (app, scope) = await PendingRowAsync(context);
        if (app is null || scope is null)
        {
            context.MarkStale();
            return;
        }
        scope.Status = status;
        await audit.RecordAsync(context.Session, OidcAuditKinds.ScopeApprovalDecided, context.DecidedBy, app.Id,
            details: new Dictionary<string, string> { ["scope"] = scope.Name, ["decision"] = status },
            ct: context.CancellationToken);
    }

    private async Task DropAsync(ApprovalDecisionContext context)
    {
        var (app, scope) = await PendingRowAsync(context);
        if (app is not null && scope is not null)
            app.Scopes.Remove(scope);
    }

    private static async Task<(OidcApplication?, OidcApplicationScope?)> PendingRowAsync(ApprovalDecisionContext context)
    {
        var app = await context.Session.LoadAsync<OidcApplication>(context.Request.Subject.Id, context.CancellationToken);
        var scope = app?.Scopes.FirstOrDefault(s =>
            string.Equals(s.Name, context.Request.Subject.Key, StringComparison.OrdinalIgnoreCase) && s.Status == OidcScopeStatuses.Pending);
        return (app, scope);
    }
}

/// <summary>Who decides a go-live request: an identity-provider administrator, live. The application's own Admin is not the reviewer.</summary>
internal sealed partial class OidcGoLiveApprovalDeciders : IApprovalDeciders<OidcGoLiveApprovalKind>
{
    [Inject] private readonly OidcPortalAccess access;

    public async ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context) => await access.IsAdministratorAsync();

    public async ValueTask<ApprovalScope> DecidesAllAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => await access.IsAdministratorAsync() ? ApprovalScope.All : ApprovalScope.None;
}

/// <summary>
/// What deciding a go-live request does: approving switches the application to Live (a deleted application makes the
/// request stale); rejecting leaves it in Development, with the reason on the request.
/// </summary>
internal sealed partial class OidcGoLiveApprovalHandler : IApprovalHandler<OidcGoLiveApprovalKind>
{
    [Inject] private readonly OidcAudit audit;

    public async Task ApproveAsync(ApprovalDecisionContext context)
    {
        if (await context.Session.LoadAsync<OidcApplication>(context.Request.Subject.Id, context.CancellationToken) is not { } app)
        {
            context.MarkStale();
            return;
        }
        app.Mode = OidcApplicationModes.Live;
        await RecordAsync(context, app.Id, ApprovalStatuses.Approved);
    }

    public async Task RejectAsync(ApprovalDecisionContext context)
    {
        if (await context.Session.LoadAsync<OidcApplication>(context.Request.Subject.Id, context.CancellationToken) is not { } app)
        {
            context.MarkStale();
            return;
        }
        await RecordAsync(context, app.Id, ApprovalStatuses.Rejected);
    }

    private Task RecordAsync(ApprovalDecisionContext context, string? applicationId, string decision)
        => audit.RecordAsync(context.Session, OidcAuditKinds.GoLiveDecided, context.DecidedBy, applicationId,
            details: new Dictionary<string, string> { ["decision"] = decision }, ct: context.CancellationToken);
}
