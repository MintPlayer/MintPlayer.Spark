using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Organizations.Services;

/// <summary>
/// What a join request (<see cref="OrganizationJoinApproval"/>) holds, shared by <see cref="SparkOrganizations.RequestToJoinAsync"/>
/// and the migration of the requests stored before D26 (<see cref="MintPlayer.Spark.Organizations.Migrations.OrganizationJoinRequestUpgrade"/>).
/// </summary>
internal static class OrganizationJoinRequests
{
    /// <summary><see cref="ApprovalSubject.Type"/> of every join request.</summary>
    public const string SubjectType = "Organization";

    /// <summary>The <see cref="ApprovalRequestInfo.Data"/> entry that holds the role asked for.</summary>
    public const string RoleKey = "role";

    /// <summary>
    /// The <see cref="ApprovalRequestInfo.Data"/> entry of a request migrated from a pre-D26 join request: the SHA-256
    /// of the token its mail carried, so the old link still finds it (<see cref="SparkOrganizations.AcceptAsync"/>).
    /// </summary>
    public const string LegacyTokenHashKey = "legacyTokenHash";

    /// <summary>The request page, app-relative: the route of the Approvals library's <c>ApprovalRequest</c> type (alias <c>approval-request</c>).</summary>
    public static string PageOf(string requestId) => "/po/approval-request/" + Uri.EscapeDataString(requestId);

    /// <summary>The subject: the organization, keyed by the requester (one pending request per user and organization).</summary>
    public static ApprovalSubject SubjectOf(string organizationId, string requesterUserId) => new(SubjectType, organizationId, requesterUserId);

    public static string Title(string requester, string organization) => $"{requester} asks to join {organization}";

    public static string Summary(string requester, string? email, string organization, OrganizationRole role)
        => string.IsNullOrEmpty(email)
            ? $"{requester} asks to join {organization} as {role}."
            : $"{requester} ({email}) asks to join {organization} as {role}.";

    /// <summary>The organization's page, app-relative.</summary>
    public static string OrganizationUrl(string organizationId) => "/po/organization/" + Uri.EscapeDataString(organizationId);

    /// <summary>The role a request asks for; never <see cref="OrganizationRole.Owner"/> (an Owner asked for joins as Admin, as before D26).</summary>
    public static OrganizationRole RoleOf(IReadOnlyDictionary<string, string?> data)
        => data.TryGetValue(RoleKey, out var value) && Enum.TryParse<OrganizationRole>(value, ignoreCase: false, out var role) && Enum.IsDefined(role)
            ? role is OrganizationRole.Owner ? OrganizationRole.Admin : role
            : OrganizationRole.Member;

    public static ApprovalSubmission Submission(Organization organization, SparkUser requester, OrganizationRole role, TimeSpan decideWithin)
    {
        var name = requester.UserName ?? requester.Email ?? requester.Id!;
        return new ApprovalSubmission
        {
            Subject = SubjectOf(organization.Id!, requester.Id!),
            Title = Title(name, organization.Name),
            Summary = Summary(name, requester.Email, organization.Name, role),
            Url = OrganizationUrl(organization.Id!),
            RequestedBy = requester.Id,
            OrganizationId = organization.Id,
            DecideWithin = decideWithin,
            Data = new Dictionary<string, string?> { [RoleKey] = role.ToString() },
            // A request from the same user replaces their pending one (the subject's key is the requester).
            ReplacePending = true,
        };
    }

    /// <summary>The Owners and Admins of <paramref name="organizationId"/>, read from storage.</summary>
    public static async Task<List<string>> DeciderIdsAsync(IAsyncDocumentSession session, string organizationId, CancellationToken cancellationToken)
        => (await SparkOrganizations.MembersAsync(session, organizationId, cancellationToken))
            .Where(m => m.Role >= OrganizationRole.Admin && m.UserId is { Length: > 0 })
            .Select(m => m.UserId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// Who decides a join request: a member of the organization with the role Admin or Owner, read from storage at decision
/// time (a member demoted since cannot decide any more). Every Owner and Admin is listed, so each is told and sees the
/// request in their inbox (before D26 only the Owners were mailed, and Admins could not approve in-app).
/// </summary>
internal sealed class OrganizationJoinDeciders : IApprovalDeciders<OrganizationJoinApproval>
{
    public async ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context)
    {
        if (context.Request.Subject.Id is not { Length: > 0 } organizationId)
            return false;
        var membership = await context.Session.LoadAsync<OrganizationMembership>(
            OrganizationMembership.IdFor(context.UserId, organizationId), context.CancellationToken);
        return membership is not null && membership.Role >= OrganizationRole.Admin;
    }

    public async ValueTask<IReadOnlyCollection<string>> GetDeciderIdsAsync(ApprovalSubject subject, IAsyncDocumentSession session, CancellationToken cancellationToken)
        => subject.Id is { Length: > 0 } organizationId
            ? await OrganizationJoinRequests.DeciderIdsAsync(session, organizationId, cancellationToken)
            : [];
}

/// <summary>
/// What deciding a join request does. Approving makes the requester a member with the role asked for (an Owner asked
/// for joins as Admin), in the decision's commit; the request goes stale instead when the organization or the
/// requester is gone, or the requester is a member already. Declining changes nothing but the request: the engine
/// keeps the reason and tells the requester.
/// </summary>
internal sealed class OrganizationJoinHandler(OrganizationMembershipReader reader, TimeProvider timeProvider) : IApprovalHandler<OrganizationJoinApproval>
{
    public async Task ApproveAsync(ApprovalDecisionContext context)
    {
        var session = context.Session;
        var cancellationToken = context.CancellationToken;
        var organizationId = context.Request.Subject.Id;
        if (context.Request.RequestedBy is not { Length: > 0 } requesterId
            || await session.LoadAsync<Organization>(organizationId, cancellationToken) is null
            || await session.LoadAsync<SparkUser>(requesterId, cancellationToken) is not { } requester)
        {
            context.MarkStale();
            return;
        }

        var membershipId = OrganizationMembership.IdFor(requesterId, organizationId);
        if (await session.LoadAsync<OrganizationMembership>(membershipId, cancellationToken) is not null)
        {
            context.MarkStale();
            return;
        }

        var membership = SparkOrganizations.NewMembership(organizationId, requester, OrganizationJoinRequests.RoleOf(context.Request.Data), timeProvider.GetUtcNow().UtcDateTime);
        // An empty change vector: the membership must not exist yet when the decision commits.
        await session.StoreAsync(membership, string.Empty, membershipId, cancellationToken);
        reader.Invalidate(requesterId);
    }

    public Task RejectAsync(ApprovalDecisionContext context) => Task.CompletedTask;
}
