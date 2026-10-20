using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Organizations.Services;

namespace MintPlayer.Spark.Organizations.Interceptors;

/// <summary>
/// Publishing is its own right (<c>docs/commerce_PRD.md</c> X8): a save through the persistent-object pipeline that
/// publishes or unpublishes an <see cref="IOrganizationPublishable"/> row, creates one published, or moves a
/// published row to another organization, needs the <c>Publish</c> role in the row's (new) organization
/// (<see cref="RequiresOrganizationRoleAttribute.For"/>; it follows <c>Edit</c> unless declared). A caller without
/// it gets a validation error on <c>IsPublished</c>; they are a member who may edit the row, so nothing is hidden.
/// </summary>
/// <remarks>
/// Runs in <see cref="InterceptorStage.Finalize"/>, after every other interceptor (the organization default
/// included), so it judges the row as it will be written. The system context is not limited.
/// </remarks>
internal sealed class OrganizationPublishGate(OrganizationMembershipReader memberships) : IBeforeSave<IOrganizationPublishable>
{
    InterceptorStage IBeforeSave.Stage => InterceptorStage.Finalize;

    public async ValueTask OnBeforeSaveAsync(IOrganizationPublishable entity, SaveContext context)
    {
        if (context.IsSystemContext)
            return;

        var before = context.Before as IOrganizationPublishable;
        var changesPublication = before is null
            ? entity.IsPublished
            : before.IsPublished != entity.IsPublished
                || (entity.IsPublished && !string.Equals(before.OrganizationId, entity.OrganizationId, StringComparison.OrdinalIgnoreCase));
        if (!changesPublication)
            return;

        var requirement = RequiresOrganizationRoleAttribute.For(context.EntityType, "Publish");
        var userId = OrganizationMembershipInterceptors.CallerId(context);
        var organizationIds = new[] { entity.OrganizationId, before?.IsPublished == true ? before.OrganizationId : null }
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var organizationId in organizationIds)
        {
            if (userId is null || !await memberships.MeetsAsync(userId, organizationId!, requirement))
                throw new SparkValidationException("You may not publish or unpublish this organization's rows.", nameof(IOrganizationPublishable.IsPublished));
        }
        if (entity.IsPublished && string.IsNullOrEmpty(entity.OrganizationId))
            throw new SparkRowLevelAccessDeniedException("Publish/" + context.EntityType.Name);
    }
}
