using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Organizations.Services;

namespace MintPlayer.Spark.Organizations.Endpoints;

/// <summary>The body of <c>POST /spark/organizations/current</c>: the organization to show, or null for all of them.</summary>
public sealed record CurrentOrganizationBody(string? OrganizationId);

/// <summary>
/// The organization switcher's state (<c>docs/seo_analyzer_PRD.md</c> Q-M11a): the caller's choice and their
/// organizations. A remembered choice the caller is no longer a member of is forgotten here, so the switcher
/// shows "All organizations".
/// </summary>
[MemberOf<OrganizationsGroup>]
internal sealed partial class GetCurrentOrganization : IGetEndpoint
{
    public static string Path => "/current";

    [Inject] private readonly CurrentOrganization current;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId)
            return TypedResults.Unauthorized();

        var response = await current.DescribeAsync(userId, httpContext.RequestAborted);
        if (response.Current is null && current.HasCookie(httpContext))
            current.Remember(httpContext, null);
        return TypedResults.Ok(response);
    }
}

/// <summary>
/// Picks the organization lists show (<c>null</c>: all of them). Only an organization the caller is a member of;
/// any other id answers the missing-row 404 (#453), whether or not it exists.
/// </summary>
[MemberOf<OrganizationsGroup>]
internal sealed partial class SetCurrentOrganization : IPostEndpoint<CurrentOrganizationBody>
{
    public static string Path => "/current";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly CurrentOrganization current;
    [Inject] private readonly OrganizationMembershipReader memberships;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(CurrentOrganizationBody request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        if (httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId)
            return TypedResults.Unauthorized();

        string? chosen = null;
        if (request.OrganizationId is { Length: > 0 } wanted)
        {
            var roles = await memberships.GetAsync(userId, cancellationToken);
            chosen = roles.Keys.FirstOrDefault(id => string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
                return TypedResults.NotFound();
        }

        current.Remember(httpContext, chosen);
        var response = await current.DescribeAsync(userId, cancellationToken);
        // The cookie reaches the server from the next request on; this answer states the choice just made.
        return TypedResults.Ok(response with { Current = chosen });
    }
}
