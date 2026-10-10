using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Organizations.Services;

namespace MintPlayer.Spark.Organizations.Interceptors;

/// <summary>
/// A new <see cref="IOrganizationOwned"/> row whose form names no organization lands in the one the caller
/// chose in the switcher (where they hold the type's <c>New</c> role), else in their personal organization
/// (<c>docs/seo_analyzer_PRD.md</c> D21, Q-M11a). Creates through the persistent-object pipeline only, by a
/// signed-in caller; edits and server code (raw sessions, the system context) are left alone. WITH CHECK still
/// judges the result, as for any organization the form names.
/// </summary>
/// <remarks>
/// Before-interceptors run in registration order, so an application interceptor that reads
/// <c>OrganizationId</c> on create and is registered before <c>AddOrganizations()</c> states
/// <see cref="InterceptorStage.Finalize"/> to see the default.
/// </remarks>
internal sealed class OrganizationOwnedDefaults(CurrentOrganization current) : IBeforeSave<IOrganizationOwned>
{
    public async ValueTask OnBeforeSaveAsync(IOrganizationOwned entity, SaveContext context)
    {
        if (context.Before is not null || context.IsSystemContext || !string.IsNullOrEmpty(entity.OrganizationId))
            return;
        if (OrganizationMembershipInterceptors.CallerId(context) is not { } userId)
            return;

        entity.OrganizationId = await current.DefaultForNewAsync(context.EntityType, userId);
    }
}
