using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints;

/// <summary>
/// The single place that decides what an access endpoint says when it refuses.
/// </summary>
/// <remarks>
/// <para>
/// Security-audit finding <b>M-3</b>: a 403 tells an unauthorized caller that the thing they
/// asked for exists, and a 404 tells them it does not. Probing ids, entity types or query
/// names then maps out data the caller was never allowed to see. The fix is that the status
/// must be a function of <i>the caller</i> and never of <i>the resource's existence</i>.
/// </para>
/// <para>
/// So, for every access endpoint (<c>/spark/po/*</c>, <c>/spark/actions/*</c>,
/// <c>/spark/lookupref/*</c>):
/// </para>
/// <list type="bullet">
/// <item><b>Anonymous</b> — 401, always, whether or not the resource exists. This is not an
/// oracle: authorization is evaluated against the principal alone and <i>before</i> anything is
/// loaded, so the answer is identical for a real id, a nonexistent one and a typo. It is also
/// load-bearing — <c>ng-spark-auth</c>'s interceptor turns a 401 into the login redirect, and
/// nothing else will.</item>
/// <item><b>Authenticated but denied</b> — 404, with a body <b>byte-identical</b> to the genuine
/// not-found for that endpoint. Equal status is not enough; a differing message is the same
/// oracle in a different field.</item>
/// <item><b>Unknown entity type or query</b> — the same shape as denied. Otherwise the status
/// still discloses which model files exist and are queryable, which is a map of the
/// application's data surface recovered one probe at a time. The cost is accepted and real:
/// <c>GET /spark/po/Bogus</c> answers 401 to an anonymous caller, about a type that will never
/// exist.</item>
/// </list>
/// <para>
/// Catalogue endpoints (<c>/spark/types</c>, <c>/spark/queries</c>, …) are the exception and do
/// not use this: the shell loads them on boot for every visitor, so a 401 would bounce anonymous
/// visitors to the sign-in page merely for opening a page. They answer a filtered 200, or 404
/// for a single item — see <c>Queries/Get.cs</c>.
/// </para>
/// <para>
/// Row-level denials already behave this way (<c>DatabaseAccess</c> returns <c>null</c>, which
/// becomes the endpoint's own 404) and always have. This brings the type level into line, so
/// that "denied the whole type" and "denied this one row" stop reporting differently.
/// </para>
/// </remarks>
internal static class SparkDenial
{
    /// <summary>
    /// The one message every refusal on an access endpoint carries.
    /// </summary>
    /// <remarks>
    /// Neutral and constant on purpose. Equal status codes are not enough: "Entity type 'Car'
    /// not found" versus "Object with ID cars/9 not found" re-opens the very same oracle one
    /// field lower, telling the caller whether the TYPE existed. Do not make this specific,
    /// and do not interpolate the requested id into it.
    /// </remarks>
    public const string NotFoundMessage = "Not found";

    /// <summary>
    /// What an access endpoint returns when it will not serve the request — because the caller
    /// is denied, because the entity type or query does not exist, or because the row does not.
    /// All three are deliberately indistinguishable.
    /// </summary>
    public static (object Body, int StatusCode) Refuse(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true || !AuthenticatingWouldHelp(httpContext))
            return (new { error = NotFoundMessage }, StatusCodes.Status404NotFound);

        return (new { error = "Authentication required" }, StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Whether telling this caller to authenticate is honest.
    /// </summary>
    /// <remarks>
    /// Under <c>spark.AllowAnonymousAccess()</c> an anonymous caller IS an authorized principal,
    /// so a 401 would be a lie — signing in changes nothing, and the client would bounce the
    /// visitor to a sign-in page that cannot help. Such an app answers 404 to everyone, which is
    /// also the stronger position: with no principal to distinguish, every refusal looks alike.
    /// </remarks>
    private static bool AuthenticatingWouldHelp(HttpContext httpContext)
        => httpContext.RequestServices.GetService<IAccessControl>() is not AllowAllAccessControl;

    /// <summary>Shorthand for endpoints returning <see cref="Results"/> directly.</summary>
    public static IResult RefuseJson(HttpContext httpContext)
    {
        var (body, status) = Refuse(httpContext);
        return Results.Json(body, statusCode: status);
    }
}
