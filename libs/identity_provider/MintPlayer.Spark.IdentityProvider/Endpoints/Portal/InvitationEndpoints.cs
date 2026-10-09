using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Portal;

/// <summary>The body of <c>POST /spark/identity-provider/invitations/accept</c>: the two values the mailed link carries.</summary>
public sealed record OidcAcceptInvitationBody(string ApplicationId, string Token);

/// <summary>What accepting answers: the application joined, or why not.</summary>
public sealed record OidcAcceptInvitationResponse(bool Accepted, string? Application, string? Problem);

/// <summary>
/// Accepts an invitation to an application's team (D3), from the SPA page the mailed link opens
/// (<c>/developers/invitations/{token}?app=…</c>). The signed-in account must be the one the address
/// belongs to; every failure answers in the same words, so the endpoint tells nothing about who was invited.
/// </summary>
[MemberOf<IdentityProviderPortalGroup>]
internal sealed partial class AcceptInvitation : IPostEndpoint<OidcAcceptInvitationBody>
{
    public static string Path => "/invitations/accept";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly OidcInvitations invitations;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(OidcAcceptInvitationBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return TypedResults.Unauthorized();
        if (string.IsNullOrEmpty(request.ApplicationId) || string.IsNullOrEmpty(request.Token)
            || !request.ApplicationId.StartsWith("OidcApplications/", StringComparison.OrdinalIgnoreCase))
            return TypedResults.Ok(new OidcAcceptInvitationResponse(false, null, "This invitation is not valid."));

        var outcome = await invitations.AcceptAsync(session, request.ApplicationId, request.Token, userId, cancellationToken);
        if (outcome.Accepted)
        {
            await audit.RecordAsync(session, OidcAuditKinds.MemberAccepted, userId, request.ApplicationId, userId,
                http.Connection.RemoteIpAddress?.ToString(), ct: cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(new OidcAcceptInvitationResponse(outcome.Accepted, outcome.ApplicationName, outcome.Problem));
    }
}
