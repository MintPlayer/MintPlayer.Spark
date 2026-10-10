using System.Security.Claims;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Organizations.Services;

namespace MintPlayer.Spark.Organizations.Interceptors;

/// <summary>
/// The rules of organizations and memberships on the persistent-object pipeline (the generic UI's
/// create, edit and delete), on top of the row policy that already limited who reaches each row
/// (<c>docs/seo_analyzer_PRD.md</c> D21–D23, D26):
/// <list type="bullet">
/// <item>a new organization gets its id and its creator as Owner, in the same commit; nobody posts
/// <c>IsPersonal</c>, <c>CreatedBy</c> or <c>CreatedAtUtc</c>;</item>
/// <item>deleting an organization cascades (D26): the application's <see cref="IOrganizationDataHandler"/>s,
/// then its memberships and invitations, all in the delete's commit; a personal one is never deleted here;</item>
/// <item>a membership edit changes the role only; only an Owner gives or takes the Owner role, and the
/// last Owner can be neither demoted nor removed; a personal organization's membership is fixed.</item>
/// </list>
/// </summary>
internal sealed class OrganizationInterceptors(SparkOrganizations organizations, OrganizationMembershipReader reader, TimeProvider timeProvider)
    : IBeforeSave<Organization>, IBeforeDelete<Organization>
{
    public async ValueTask OnBeforeSaveAsync(Organization entity, SaveContext context)
    {
        entity.Name = SparkOrganizations.ValidName(entity.Name);
        if (context.Before is Organization stored)
        {
            entity.IsPersonal = stored.IsPersonal;
            entity.CreatedBy = stored.CreatedBy;
            entity.CreatedAtUtc = stored.CreatedAtUtc;
            return;
        }

        if (OrganizationMembershipInterceptors.CallerId(context) is not { } userId)
            throw new SparkRowLevelAccessDeniedException("New/Organization");

        var session = context.GetSession();
        var user = await session.LoadAsync<SparkUser>(userId)
            ?? throw new SparkRowLevelAccessDeniedException("New/Organization");

        entity.Id = Organization.NewId();
        entity.IsPersonal = false;
        entity.CreatedBy = userId;
        entity.CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        // The creator's Owner membership commits with the organization, or neither does.
        var membership = SparkOrganizations.NewMembership(entity.Id, user, OrganizationRole.Owner, entity.CreatedAtUtc.Value);
        await session.StoreAsync(membership, string.Empty, membership.Id);
        reader.Invalidate(userId);
    }

    public async ValueTask OnBeforeDeleteAsync(Organization entity, DeleteContext context)
    {
        if (entity.IsPersonal && !context.IsSystemContext)
            throw new SparkValidationException("A personal organization is deleted with the account.");

        await organizations.DeleteDependentsAsync(context.GetSession(), entity, CancellationToken.None);
        reader.Invalidate();
    }
}

/// <summary>The membership half of <see cref="OrganizationInterceptors"/>: role edits and removals.</summary>
internal sealed class OrganizationMembershipInterceptors(OrganizationMembershipReader reader)
    : IBeforeSave<OrganizationMembership>, IBeforeDelete<OrganizationMembership>
{

    public async ValueTask OnBeforeSaveAsync(OrganizationMembership entity, SaveContext context)
    {
        if (context.Before is not OrganizationMembership stored)
        {
            // Memberships come from creating an organization or accepting a link, never from a form.
            if (!context.IsSystemContext)
                throw new SparkValidationException("Members join through an invitation or a join request.");
            return;
        }

        entity.OrganizationId = stored.OrganizationId;
        entity.UserId = stored.UserId;
        entity.UserName = stored.UserName;
        entity.JoinedAtUtc = stored.JoinedAtUtc;
        if (!Enum.IsDefined(entity.Role))
            throw new SparkValidationException("Choose a role.", nameof(OrganizationMembership.Role));
        if (entity.Role == stored.Role)
            return;

        var session = context.GetSession();
        await RequireNotPersonalAsync(session, stored.OrganizationId!);
        if (entity.Role == OrganizationRole.Owner || stored.Role == OrganizationRole.Owner)
        {
            await RequireOwnerAsync(session, context, stored.OrganizationId!, stored);
            if (stored.Role == OrganizationRole.Owner && !await SparkOrganizations.HasOtherOwnerAsync(session, stored.OrganizationId!, stored.UserId!, CancellationToken.None))
                throw new SparkValidationException(SparkOrganizations.LastOwnerProblem, nameof(OrganizationMembership.Role));
        }

        reader.Invalidate(stored.UserId);
    }

    public async ValueTask OnBeforeDeleteAsync(OrganizationMembership entity, DeleteContext context)
    {
        var session = context.GetSession();
        await RequireNotPersonalAsync(session, entity.OrganizationId!);
        if (entity.Role == OrganizationRole.Owner)
        {
            await RequireOwnerAsync(session, context, entity.OrganizationId!, entity);
            if (!await SparkOrganizations.HasOtherOwnerAsync(session, entity.OrganizationId!, entity.UserId!, CancellationToken.None))
                throw new SparkValidationException(SparkOrganizations.LastOwnerProblem);
        }

        reader.Invalidate(entity.UserId);
    }

    private static async Task RequireNotPersonalAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string organizationId)
    {
        if (await session.LoadAsync<Organization>(organizationId) is { IsPersonal: true })
            throw new SparkValidationException(SparkOrganizations.PersonalProblem);
    }

    /// <summary>Only an Owner touches the Owner role. Read from storage, not the request's cache.</summary>
    private static async Task RequireOwnerAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, SparkInterceptorContext context, string organizationId, OrganizationMembership target)
    {
        if (context.IsSystemContext)
            return;
        // The caller's own row is the tracked instance being changed: judge them by its stored role.
        var role = CallerId(context) switch
        {
            null => (OrganizationRole?)null,
            var userId when string.Equals(userId, target.UserId, StringComparison.OrdinalIgnoreCase) => target.Role,
            var userId => (await session.LoadAsync<OrganizationMembership>(OrganizationMembership.IdFor(userId, organizationId)))?.Role,
        };
        if (role != OrganizationRole.Owner)
            throw new SparkValidationException("Only an Owner can give or take the Owner role.", nameof(OrganizationMembership.Role));
    }

    internal static string? CallerId(SparkInterceptorContext context)
        => context.User?.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id ? id : null;
}
