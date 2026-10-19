using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Organizations.Endpoints;

/// <summary>The package's API under <c>/spark/organizations</c>. Signed-in users only.</summary>
internal class OrganizationsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/organizations";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.RequireAuthorization();
}

/// <summary>The body of <c>POST /spark/organizations/accept</c>: the two values the mailed link carries.</summary>
public sealed record AcceptOrganizationInvitationBody(string InvitationId, string Token);

/// <summary>
/// What accepting answers: the organization joined, or why not. <c>Reason</c> is <c>invalid</c> (one wording
/// for every unknown, forged, used or expired link) or <c>otherAddress</c> (a valid invitation opened by an
/// account that does not own the invited address, D43). <c>ApprovalRequestUrl</c> is set only for a join-request
/// link mailed before D26 whose token matches: the page of the approval request it became, where the client goes.
/// </summary>
public sealed record AcceptOrganizationInvitationResponse(bool Accepted, string? OrganizationId, string? Organization, string? Problem, string? Reason, string? ApprovalRequestUrl = null);

/// <summary>
/// Redeems an invitation (<c>docs/seo_analyzer_PRD.md</c> D18), from the SPA page the mailed link opens
/// (<c>/organizations/accept/{token}?id=…</c>). Accepting is this explicit POST, never the GET of the link,
/// which mail scanners open. Every refusal answers 200 with the same words, so the endpoint tells nothing about
/// which invitations exist. The one other refusal, the wrong account for a valid invitation, needs the link's
/// token, so it tells its holder only what they already know; so does the redirect of a pre-D26 join-request
/// link to its approval request.
/// </summary>
[MemberOf<OrganizationsGroup>]
internal sealed partial class AcceptOrganizationInvitation : IPostEndpoint<AcceptOrganizationInvitationBody>
{
    public static string Path => "/accept";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkOrganizations organizations;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(AcceptOrganizationInvitationBody request, CancellationToken cancellationToken)
    {
        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return TypedResults.Unauthorized();

        var outcome = await organizations.AcceptAsync(request.InvitationId ?? "", request.Token ?? "", userId, cancellationToken);
        return TypedResults.Ok(new AcceptOrganizationInvitationResponse(outcome.Accepted, outcome.OrganizationId, outcome.OrganizationName, outcome.Problem, outcome.Reason, outcome.ApprovalRequestUrl));
    }
}
