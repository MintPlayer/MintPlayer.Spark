using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Portal;

/// <summary>One granted scope on the connected-applications page, in the user's language.</summary>
public sealed record ConnectedScope(string Name, string DisplayName, string? Description, bool Required);

/// <summary>One application the user granted access to (D6).</summary>
public sealed record ConnectedApplication(
    string ApplicationId,
    string DisplayName,
    string? LogoUrl,
    string? Publisher,
    string? HomepageUrl,
    IReadOnlyList<ConnectedScope> Scopes,
    DateTime FirstGrantedAt,
    DateTime? LastUsedAt,
    bool Remembered,
    DateTime? RememberedUntil);

/// <summary>The body of <c>POST /spark/identity-provider/applications/withdraw</c>.</summary>
/// <param name="Scopes">The scopes to withdraw; null or empty withdraws the whole grant.</param>
public sealed record WithdrawAccessBody(string ApplicationId, string[]? Scopes);

/// <summary>The signed-in user's connected applications, for the SPA's <c>account/applications</c> page (D6, D7).</summary>
[MemberOf<IdentityProviderPortalGroup>]
internal sealed partial class GetConnectedApplications : IGetEndpoint
{
    public static string Path => "/applications";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ConnectText text;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return TypedResults.Unauthorized();

        var ct = httpContext.RequestAborted;
        using var session = store.OpenAsyncSession();
        var grants = await session.Query<OidcGrant, OidcGrants_BySubject>()
            .Where(g => g.Subject == userId, exact: true)
            .ToListAsync(ct);
        grants = grants.Where(g => g.Status == "valid").ToList();

        var apps = await session.LoadAsync<OidcApplication>(grants.Select(g => g.ApplicationId).Distinct(), ct);
        var definitions = await OidcScopeCatalog.LoadAsync(session, grants.SelectMany(g => g.GrantedScopes), ct);

        var result = new List<ConnectedApplication>();
        foreach (var grant in grants.OrderBy(g => g.CreatedAt))
        {
            if (!apps.TryGetValue(grant.ApplicationId, out var app) || app is null)
                continue;
            var scopes = grant.GrantedScopes.Select(name =>
            {
                var def = definitions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
                var required = name == "openid" || def?.Required == true
                    || app.Scopes.Any(s => s.Required && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                return new ConnectedScope(name, text.Of(def?.DisplayName, name), def?.Description is null ? null : text.Of(def.Description, ""), required);
            }).ToList();
            result.Add(new ConnectedApplication(app.Id!, app.DisplayName, app.LogoUrl, app.Publisher, app.HomepageUrl, scopes,
                grant.CreatedAt, grant.LastUsedAt, grant.Remembered, grant.ExpiresAt));
        }

        return TypedResults.Ok(result);
    }
}

/// <summary>Withdraws access from an application, wholly or per scope (D6, Q5).</summary>
[MemberOf<IdentityProviderPortalGroup>]
internal sealed partial class WithdrawApplicationAccess : IPostEndpoint<WithdrawAccessBody>
{
    public static string Path => "/applications/withdraw";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly OidcGrantWithdrawal withdrawal;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(WithdrawAccessBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return TypedResults.Unauthorized();
        if (string.IsNullOrEmpty(request.ApplicationId))
            return TypedResults.BadRequest();

        var done = await withdrawal.WithdrawAsync(userId, request.ApplicationId,
            request.Scopes is { Length: > 0 } scopes ? scopes : null,
            http.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        return done ? TypedResults.NoContent() : TypedResults.Problem("Access could not be withdrawn. Try again.", statusCode: StatusCodes.Status409Conflict);
    }
}
