using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Portal;

/// <summary>
/// The developer portal's API under <c>/spark/identity-provider</c>, for the SPA pages
/// (<c>docs/identity_provider_platform_PRD.md</c> D7). Signed-in users only.
/// </summary>
internal class IdentityProviderPortalGroup : IEndpointGroup
{
    public static string Prefix => "/spark/identity-provider";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.RequireAuthorization();
}

/// <summary>What <c>GET /spark/identity-provider/developer</c> answers.</summary>
public sealed record OidcDeveloperStatusResponse(
    string? Status,
    int? AcceptedTermsVersion,
    int CurrentTermsVersion,
    string? TermsUrl,
    bool RequireApproval,
    bool IsActive,
    DateTime? RequestedAt,
    DateTime? DecidedAt,
    string? Note);

/// <summary>The body of <c>POST /spark/identity-provider/developer</c>.</summary>
public sealed record OidcDeveloperRequestBody(int TermsVersion);

/// <summary>
/// Issues an initial access token for dynamic client registration (RFC 7591, D8), shown once. Approved
/// developers only: registration is the portal by protocol.
/// </summary>
[MemberOf<IdentityProviderPortalGroup>]
internal sealed partial class IssueRegistrationToken : IPostEndpoint
{
    public static string Path => "/developer/registration-token";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly OidcDevelopers developers;
    [Inject] private readonly IAsyncDocumentSession session;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId) || !developers.IsActive(await developers.GetAsync(userId, httpContext.RequestAborted)))
            return TypedResults.Forbid();

        var token = await OidcClientRegistration.IssueInitialAccessTokenAsync(session, userId, httpContext.RequestAborted);
        await session.SaveChangesAsync(httpContext.RequestAborted);
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new { initialAccessToken = token, expiresIn = (int)OidcClientRegistration.InitialTokenLifetime.TotalSeconds });
    }
}

/// <summary>The signed-in user's developer status, for the <c>/developers</c> page.</summary>
[MemberOf<IdentityProviderPortalGroup>]
internal sealed partial class GetDeveloperStatus : IGetEndpoint
{
    public static string Path => "/developer";

    [Inject] private readonly OidcDevelopers developers;
    [Inject] private readonly SparkIdentityProviderOptions options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return TypedResults.Unauthorized();

        var developer = await developers.GetAsync(userId, httpContext.RequestAborted);
        return TypedResults.Ok(new OidcDeveloperStatusResponse(
            developer?.Status,
            developer?.TermsVersion,
            options.Developers.TermsVersion,
            options.Developers.TermsUrl,
            options.Developers.RequireApproval,
            developers.IsActive(developer),
            developer?.RequestedAt,
            developer?.DecidedAt,
            developer?.Note));
    }
}

/// <summary>
/// Requests developer status by accepting the current terms (D2), or accepts raised terms again.
/// Without <c>RequireApproval</c> the user is a developer at once.
/// </summary>
[MemberOf<IdentityProviderPortalGroup>]
internal sealed partial class RequestDeveloperStatus : IPostEndpoint<OidcDeveloperRequestBody>
{
    public static string Path => "/developer";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly OidcDevelopers developers;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly SparkIdentityProviderOptions options;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(OidcDeveloperRequestBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return TypedResults.Unauthorized();

        // The user accepts the terms they were shown. A page loaded before the terms were raised
        // posts the old version, and accepting what nobody showed them is not consent.
        if (request.TermsVersion != options.Developers.TermsVersion)
            return TypedResults.Problem(
                "The developer terms changed since this page was loaded. Read them again and accept the current version.",
                statusCode: StatusCodes.Status409Conflict);

        var now = DateTime.UtcNow;
        var developer = await developers.GetAsync(userId, cancellationToken);
        switch (developer?.Status)
        {
            case OidcDeveloperStatuses.Approved:
                // Re-accepting raised terms: the approval stands.
                developer.TermsVersion = request.TermsVersion;
                break;
            case OidcDeveloperStatuses.Requested:
                developer.TermsVersion = request.TermsVersion;
                break;
            case OidcDeveloperStatuses.Revoked:
                // An administrator took developer status away; asking again does not undo that.
                return TypedResults.Problem("Your developer status was revoked. Contact an administrator.", statusCode: StatusCodes.Status403Forbidden);
            default:
                // First request, or a new one after a rejection.
                developer = new OidcDeveloper
                {
                    Status = options.Developers.RequireApproval ? OidcDeveloperStatuses.Requested : OidcDeveloperStatuses.Approved,
                    TermsVersion = request.TermsVersion,
                    RequestedAt = now,
                    DecidedAt = options.Developers.RequireApproval ? null : now,
                };
                await audit.RecordAsync(session, OidcAuditKinds.DeveloperRequested, userId, subjectId: userId,
                    ipAddress: http.Connection.RemoteIpAddress?.ToString(),
                    details: new Dictionary<string, string> { ["autoApproved"] = (!options.Developers.RequireApproval).ToString() },
                    ct: cancellationToken);
                break;
        }

        OidcDevelopers.Set(session, userId, developer);
        await session.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new OidcDeveloperStatusResponse(
            developer.Status, developer.TermsVersion, options.Developers.TermsVersion, options.Developers.TermsUrl,
            options.Developers.RequireApproval, developers.IsActive(developer), developer.RequestedAt, developer.DecidedAt, developer.Note));
    }
}
