using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// <c>POST /connect/register</c> (RFC 7591): registers a client with an initial access token. The token
/// is a developer's, from the portal, and the developer must still be approved: registration is the
/// developer portal by protocol, not a back door around it.
/// </summary>
/// <remarks>
/// Mapped when someone may register here: a developer (<c>Endpoints.DeveloperRegistration</c>) or an AI agent, anonymously
/// with loopback redirects (the agent server's <c>DynamicRegistration</c>). The RFC 7592 endpoints below are the developer's
/// only: an anonymous registration gets no registration access token.
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcRegisterClient : IPostEndpoint<OidcClientMetadata>
{
    public static string Path => "/register";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => OidcSurface.Of(services).Registration;

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcClientRegistration registration;
    [Inject] private readonly OidcDevelopers developers;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;
    [Inject] private readonly Mcp.OidcAgentDynamicRegistration agentRegistration;
    [Inject] private readonly OidcSurface surface;
    [Inject] private readonly ISparkApprovals approvals;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.Json(new { error = "invalid_client_metadata", error_description = failure?.Message }, statusCode: 400));

    // The machine-endpoint budget (per IP, when the application runs the rate limiter), on top of the always-on
    // per-address registration budget of anonymous agent registration (MCP plan M3b).
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireRateLimiting(Extensions.SparkIdentityProviderExtensions.RateLimitPolicy);

    public override async Task<IResult> HandleAsync(OidcClientMetadata metadata, CancellationToken ct)
    {
        var http = httpContextAccessor.HttpContext!;
        using var session = store.OpenAsyncSession();

        // MCP PRD D5 (G6): with idp.ForMcp(), a request WITHOUT an initial access token registers an AI agent with
        // loopback redirects only. A request that presents a token is still a developer's, and is judged as one.
        if (options.Mcp is { DynamicRegistration: true } && http.Request.Headers.Authorization.Count == 0)
        {
            http.Response.Headers.CacheControl = "no-store";
            return await agentRegistration.RegisterAsync(metadata, http.Connection.RemoteIpAddress?.ToString(), ct);
        }

        // A provider without developer registration (Endpoints.DeveloperRegistration, e.g. AgentsOnly()) maps this endpoint
        // for agents only: an initial access token, even a valid one issued before, registers nothing here.
        var initial = surface.DeveloperRegistration
            ? await OidcClientRegistration.ResolveBearerAsync(session, http.Request, "initial", ct)
            : null;
        if (initial is null || !developers.IsActive(await developers.GetAsync(initial.Subject, ct)))
        {
            http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return Results.Json(new { error = "invalid_token" }, statusCode: 401);
        }

        var app = new OidcApplication
        {
            Id = "OidcApplications/" + Guid.NewGuid().ToString("N"),
            ClientId = OidcClientIdReservation.GenerateClientId(),
            Mode = OidcApplicationModes.Development,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = initial.Subject,
            Members = [new OidcApplicationMember
            {
                MemberId = Guid.NewGuid().ToString("N"), UserId = initial.Subject, Role = OidcMemberRoles.Admin,
                Status = OidcMemberStatuses.Active, AcceptedAt = DateTime.UtcNow,
            }],
        };

        try { registration.Apply(app, metadata); }
        catch (SparkValidationException ex) { return Invalid(ex.Message); }

        string? secret = null;
        if (app.ClientType == "confidential" && app.TokenEndpointAuthMethod is OidcClientAuthMethods.SecretBasic or OidcClientAuthMethods.SecretPost)
        {
            secret = OpaqueHandle.Generate();
            app.Secrets.Add(new ClientSecret { SecretId = Guid.NewGuid().ToString("N")[..12], Hash = ClientSecretHasher.Hash(secret), CreatedAt = DateTime.UtcNow, Description = "Dynamic registration" });
        }

        // One request per cross-owner scope, committed with the application (the registration's own session).
        await OidcApprovalRequests.RaiseScopeRequestsAsync(approvals, session, app, app.Scopes, initial.Subject, ct);
        await OidcClientIdReservation.ReserveAsync(session, app);
        await session.StoreAsync(app, ct);

        // RFC 7592: the client manages its registration with a token of its own.
        var registrationToken = OidcTokenReference.GenerateValue();
        await session.StoreExpiringAsync(new OidcToken
        {
            Id = OidcTokenReference.DocumentId("rat:" + registrationToken),
            Type = OidcTokenTypes.RegistrationAccessToken,
            ApplicationId = app.Id,
            Subject = initial.Subject,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddYears(10),
            Properties = { ["kind"] = "client" },
        }, ct);
        await audit.RecordAsync(session, OidcAuditKinds.DynamicRegistration, initial.Subject, app.Id, ipAddress: http.Connection.RemoteIpAddress?.ToString(), ct: ct);
        await session.SaveChangesAsync(ct);

        var response = OidcClientRegistration.Describe(app, oidcIssuer.Resolve(http.Request));
        response["registration_access_token"] = registrationToken;
        if (secret is not null)
        {
            response["client_secret"] = secret;
            response["client_secret_expires_at"] = 0;
        }
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(response, statusCode: StatusCodes.Status201Created);
    }

    internal static IResult Invalid(string description)
        => Results.Json(new { error = "invalid_client_metadata", error_description = description }, statusCode: 400);
}

/// <summary>RFC 7592 §2.1: a client reads its registration with its registration access token.</summary>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcReadClientRegistration : IGetEndpoint
{
    public static string Path => "/register/{clientId}";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => OidcSurface.Of(services).DeveloperRegistration;

    [RouteParam] public string ClientId { get; set; } = "";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcIssuer oidcIssuer;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        using var session = store.OpenAsyncSession();
        if (await OidcRegistrationAccess.LoadAsync(session, httpContext, ClientId) is not { } app)
            return OidcRegistrationAccess.Unauthorized(httpContext);
        return Results.Json(OidcClientRegistration.Describe(app, oidcIssuer.Resolve(httpContext.Request)));
    }
}

/// <summary>RFC 7592 §2.2: a client replaces its metadata.</summary>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcUpdateClientRegistration : IPutEndpoint<OidcClientMetadata>
{
    public static string Path => "/register/{clientId}";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => OidcSurface.Of(services).DeveloperRegistration;

    [RouteParam] public string ClientId { get; set; } = "";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcClientRegistration registration;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ISparkApprovals approvals;

    public override async Task<IResult> HandleAsync(OidcClientMetadata metadata, CancellationToken ct)
    {
        var http = httpContextAccessor.HttpContext!;
        using var session = store.OpenAsyncSession();
        if (await OidcRegistrationAccess.LoadAsync(session, http, ClientId) is not { } app)
            return OidcRegistrationAccess.Unauthorized(http);

        var before = app.Scopes.ToList();
        try { registration.Apply(app, metadata); }
        catch (SparkValidationException ex) { return OidcRegisterClient.Invalid(ex.Message); }
        // Kept scopes keep their decided status (Apply); a dropped one's pending request is withdrawn, and each new
        // cross-owner scope raises one, all in this session.
        await OidcApprovalRequests.ClosePendingScopeRequestsAsync(approvals, session, app.Id!,
            before.Where(b => !app.Scopes.Any(s => string.Equals(s.Name, b.Name, StringComparison.OrdinalIgnoreCase))), ct);
        await OidcApprovalRequests.RaiseScopeRequestsAsync(approvals, session, app,
            app.Scopes.Where(s => !before.Any(b => string.Equals(b.Name, s.Name, StringComparison.OrdinalIgnoreCase))), app.CreatedBy, ct);

        await audit.RecordAsync(session, OidcAuditKinds.ApplicationChanged, app.CreatedBy, app.Id, ipAddress: http.Connection.RemoteIpAddress?.ToString(),
            details: new Dictionary<string, string> { ["via"] = "registration" }, ct: ct);
        await session.SaveChangesAsync(ct);
        return Results.Json(OidcClientRegistration.Describe(app, oidcIssuer.Resolve(http.Request)));
    }
}

/// <summary>RFC 7592 §2.3: a client deregisters itself.</summary>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcDeleteClientRegistration : IDeleteEndpoint
{
    public static string Path => "/register/{clientId}";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => OidcSurface.Of(services).DeveloperRegistration;

    [RouteParam] public string ClientId { get; set; } = "";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly ISparkApprovals approvals;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        using var session = store.OpenAsyncSession();
        if (await OidcRegistrationAccess.LoadAsync(session, httpContext, ClientId) is not { } app)
            return OidcRegistrationAccess.Unauthorized(httpContext);

        // The client's pending scope and go-live requests go with it (as on the PO delete, OidcApplicationInterceptors).
        await approvals.ClosePendingAsync<OidcScopeApprovalKind>(session, OidcApprovalRequests.ApplicationSubject(app.Id!), ct);
        await approvals.ClosePendingAsync<OidcGoLiveApprovalKind>(session, OidcApprovalRequests.ApplicationSubject(app.Id!), ct);
        session.Delete(app);
        await audit.RecordAsync(session, OidcAuditKinds.ApplicationDeleted, app.CreatedBy, app.Id, ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);
        await session.SaveChangesAsync(ct);
        await OidcClientIdReservation.ReleaseAsync(session, app);
        return Results.NoContent();
    }
}

internal static class OidcRegistrationAccess
{
    /// <summary>The application the request's registration access token manages, when it names <paramref name="clientId"/>.</summary>
    public static async Task<OidcApplication?> LoadAsync(IAsyncDocumentSession session, HttpContext http, string clientId, CancellationToken ct = default)
    {
        var token = await OidcClientRegistration.ResolveBearerAsync(session, http.Request, "client", ct);
        if (token?.ApplicationId is not { Length: > 0 } applicationId)
            return null;
        var app = await session.LoadAsync<OidcApplication>(applicationId, ct);
        return app is not null && string.Equals(app.ClientId, clientId, StringComparison.Ordinal) ? app : null;
    }

    /// <summary>RFC 7592 §2: an invalid token and a client it does not manage look the same.</summary>
    public static IResult Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Results.Json(new { error = "invalid_token" }, statusCode: 401);
    }
}
