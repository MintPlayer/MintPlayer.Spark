using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Requests;
using MintPlayer.Spark.Endpoints;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Contributions;

/// <summary>The body of <c>POST /spark/po/revert-contribution</c>.</summary>
internal sealed class RevertContributionRequest : ISparkTypedRequest
{
    /// <summary>The generated contribution type (its model id or alias).</summary>
    public string? ObjectTypeId { get; set; }

    /// <summary>The contribution to make current.</summary>
    public string? Id { get; set; }
}

/// <summary>
/// The endpoint Contributions adds (an add-on endpoint, like History's revert): custom actions are
/// declared by the app's <c>customActions.json</c>, so a library verb is served by its own route.
/// </summary>
internal static class ContributionEndpoints
{
    /// <summary>The route of <see cref="ContributionRights.RevertContribution"/>.</summary>
    public const string RevertPath = "/spark/po/revert-contribution";

    public static void Map(IEndpointRouteBuilder endpoints)
        // Cast to a typed delegate: as a RequestDelegate (the method group also fits it) the IResult would be discarded.
        => endpoints.MapPost(RevertPath, (Func<HttpContext, Task<IResult>>)RevertAsync).WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    /// <summary>
    /// <c>POST /spark/po/revert-contribution { objectTypeId, id }</c> → 200 with the reverted-to
    /// contribution. Requires <c>RevertContribution/{Type}</c> and <c>Read/{Type}</c> (plus the row
    /// gate). A refusal — no right, no such type or row, a type that is not a generated contribution
    /// type — is the standard indistinguishable 404 (401 anonymous); a hidden target or a
    /// non-soft-deletable declaration is 400; a concurrent write to the slot 409.
    /// </summary>
    internal static async Task<IResult> RevertAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var client = services.GetRequiredService<IClientAccessor>();
        var modelLoader = services.GetRequiredService<IModelLoader>();

        var (request, entityType) = await SparkAddOnEndpoints.ReadTypedRequestAsync<RevertContributionRequest>(httpContext, modelLoader);
        if (request is null || entityType is null || string.IsNullOrEmpty(request.Id))
            return SparkAddOnEndpoints.Refusal(client, httpContext);
        var handler = services.GetRequiredService<ContributionCatalog>().ForContributionClrType(entityType.ClrType);
        if (handler is null)
            return SparkAddOnEndpoints.Refusal(client, httpContext);

        try
        {
            await services.GetRequiredService<IPermissionService>()
                .EnsureAuthorizedAsync(ContributionRights.RevertContribution, entityType.Name, httpContext.RequestAborted);

            // Read right and row gate on the contribution itself — hidden ones included for ViewDeleted
            // holders (SoftDelete honours the flag only for them), so a moderator reverting to a hidden
            // version hears "restore it first" instead of an indistinguishable 404.
            SparkAddOnEndpoints.UseDeletedFilter(httpContext, SparkDeletedFilter.Include, entityType);
            var databaseAccess = services.GetRequiredService<IDatabaseAccess>();
            if (await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id) is null)
                return SparkAddOnEndpoints.Refusal(client, httpContext);

            var interceptor = services.GetRequiredService<ContributionsInterceptor>();
            var write = interceptor.ModeratorWrite(isSystemContext: false, httpContext.User);
            var (targetId, affected) = await handler.RevertAsync(request.Id, write);
            await write.Session.SaveChangesAsync(httpContext.RequestAborted);

            var state = services.GetRequiredService<ContributionRequestState>();
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
            await state.FlushAuditsAsync(services, httpContext.RequestAborted);

            var reverted = await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id);
            // After the commit, like the save notices: the response says what the revert did.
            client.Notify(ContributionMessages.Format(services, ContributionMessages.Reverted, affected.Count), NotificationKind.Success);
            return SparkAddOnEndpoints.Envelope(client, reverted, StatusCodes.Status200OK);
        }
        catch (ConcurrencyException)
        {
            return SparkAddOnEndpoints.ConcurrencyConflict(client);
        }
        catch (Exception ex) when (SparkAddOnEndpoints.IsConcurrencyConflict(ex))
        {
            return SparkAddOnEndpoints.ConcurrencyConflict(client);
        }
        catch (SparkValidationException ex)
        {
            return SparkAddOnEndpoints.ValidationFailed(client, ex);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkAddOnEndpoints.Refusal(client, httpContext);
        }
    }
}
