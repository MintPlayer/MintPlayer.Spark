using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Requests;
using MintPlayer.Spark.Endpoints;
using MintPlayer.Spark.Services;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// <c>/spark/po</c>, shared with core's persistent-object endpoints and SoftDelete's: the route table
/// is fully literal, and ids — which contain slashes — travel in the body (#460, T1).
/// </summary>
internal class ContributionsPersistentObjectGroup : IEndpointGroup
{
    public static string Prefix => "/spark/po";
}

/// <summary>The body of <c>POST /spark/po/revert-contribution</c>.</summary>
internal sealed class RevertContributionRequest : ISparkTypedRequest
{
    /// <summary>The generated contribution type (its model id or alias).</summary>
    public string? ObjectTypeId { get; set; }

    /// <summary>The contribution to make current.</summary>
    public string? Id { get; set; }
}

/// <summary>
/// <c>POST /spark/po/revert-contribution { objectTypeId, id }</c> → 200 with the reverted-to
/// contribution: the endpoint Contributions adds (an add-on endpoint, like History's revert), because
/// custom actions are declared by the app's <c>actions.json</c> and a library verb is served by its own
/// route. Requires <c>RevertContribution/{Type}</c> and <c>Read/{Type}</c> (plus the row gate).
/// </summary>
/// <remarks>
/// A refusal — no right, no such type or row, a type that is not a generated contribution type, or a
/// body that cannot be bound — is the standard indistinguishable 404 (401 anonymous); a hidden target
/// or a non-soft-deletable declaration is 400; a concurrent write to the slot 409.
/// </remarks>
[MemberOf<ContributionsPersistentObjectGroup>]
internal sealed partial class RevertContribution : IPostEndpoint<RevertContributionRequest>
{
    public static string Path => "/revert-contribution";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IClientAccessor client;
    [Inject] private readonly ContributionCatalog catalog;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly ContributionsInterceptor interceptor;
    [Inject] private readonly ContributionRequestState state;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    // The request scope, for the two helpers this shares with the interceptor (audit sinks, notice
    // translations); this endpoint itself resolves nothing from it.
    [Inject] private readonly IServiceProvider services;

    /// <summary>A body that cannot be bound is refused like an unknown type, never with a parse error.</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(addOn.Refusal(context));

    public override async Task<IResult> HandleAsync(RevertContributionRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;

        var entityType = addOn.ResolveType(request);
        if (entityType is null || string.IsNullOrEmpty(request.Id))
            return addOn.Refusal(httpContext);
        var handler = catalog.ForContributionClrType(entityType.ClrType);
        if (handler is null)
            return addOn.Refusal(httpContext);

        try
        {
            await permissionService.EnsureAuthorizedAsync(ContributionRights.RevertContribution, entityType.Name, cancellationToken);

            // Read right and row gate on the contribution itself — hidden ones included for ViewDeleted
            // holders (SoftDelete honours the flag only for them), so a moderator reverting to a hidden
            // version hears "restore it first" instead of an indistinguishable 404.
            addOn.UseDeletedFilter(SparkDeletedFilter.Include, entityType);
            if (await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id) is null)
                return addOn.Refusal(httpContext);

            var write = interceptor.ModeratorWrite(isSystemContext: false, httpContext.User);
            var (targetId, affected) = await handler.RevertAsync(request.Id, write);
            await write.Session.SaveChangesAsync(cancellationToken);

            state.Audits.Add(new SatelliteAuditEntry
            {
                Action = ContributionRights.RevertContribution,
                DocumentType = handler.Descriptor.ContributionType,
                DocumentId = request.Id,
                AffectedDocumentIds = affected,
                TargetType = handler.Descriptor.TargetType,
                TargetId = targetId,
                Reason = SoftDeleteBridge.RevertedReason,
                User = httpContext.User,
            });
            await state.FlushAuditsAsync(services, cancellationToken);

            var reverted = await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id);
            // After the commit, like the save notices: the response says what the revert did.
            client.Notify(ContributionMessages.Format(services, ContributionMessages.Reverted, affected.Count), NotificationKind.Success);
            return addOn.Envelope(reverted, StatusCodes.Status200OK);
        }
        catch (ConcurrencyException)
        {
            return addOn.ConcurrencyConflict();
        }
        catch (Exception ex) when (addOn.IsConcurrencyConflict(ex))
        {
            return addOn.ConcurrencyConflict(ex);
        }
        catch (SparkValidationException ex)
        {
            return addOn.ValidationFailed(ex);
        }
        catch (SparkAccessDeniedException)
        {
            return addOn.Refusal(httpContext);
        }
    }
}
