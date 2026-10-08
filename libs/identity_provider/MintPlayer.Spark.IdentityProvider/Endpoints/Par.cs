using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Pushed authorization requests (<c>POST /connect/par</c>, RFC 9126, <c>docs/identity_provider_platform_PRD.md</c> D8).
/// The client authenticates exactly as at the token endpoint and posts the authorization parameters (or a
/// signed request object); the answer is a <c>request_uri</c> the browser carries to <c>/connect/authorize</c>
/// with the client id and nothing else.
/// </summary>
/// <remarks>
/// The parameters are checked here for what can fail without a user (client, redirect URI, response type),
/// so a bad request fails at the push rather than in the user's browser, and again at authorize, where
/// every rule of a request by value applies.
/// </remarks>
[MemberOf<OidcConnectCorsGroup>]
internal sealed partial class OidcPushedAuthorization : IPostEndpoint<OidcAuthorizeParameters>
{
    public static string Path => "/par";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcClientAuthenticator clientAuthenticator;
    [Inject] private readonly OidcRequestObjects requestObjects;
    [Inject] private readonly OidcIssuer oidcIssuer;

    private HttpContext context = null!;
    private IFormCollection form = null!;

    protected override async ValueTask<OidcAuthorizeParameters?> BindRequestAsync(HttpContext context)
    {
        this.context = context;
        if (!context.Request.HasFormContentType)
            throw new EndpointBindingException(StatusCodes.Status400BadRequest, "Content-Type must be application/x-www-form-urlencoded.");
        form = await context.Request.ReadFormAsync(context.RequestAborted);
        return OidcAuthorizeParameters.FromForm(form);
    }

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.Json(new { error = "invalid_request", error_description = failure?.Message }, statusCode: StatusCodes.Status400BadRequest));

    public override async Task<IResult> HandleAsync(OidcAuthorizeParameters request, CancellationToken ct)
    {
        using var session = store.OpenAsyncSession();
        var client = await clientAuthenticator.AuthenticateAsync(context, form, session, ct);
        if (!client.Succeeded)
            return client.ToResult(context);
        var app = client.Application!;

        // RFC 9126 §2.1: request_uri cannot itself be pushed.
        if (request.RequestUri is not null)
            return Error("invalid_request", "request_uri cannot be pushed.");

        var parameters = request;
        var fromObject = false;
        if (request.Request is not null)
        {
            var parsed = await requestObjects.ParseRequestObjectAsync(session, request.Request, app.ClientId, oidcIssuer.Resolve(context.Request), ct);
            if (parsed.Error is not null)
                return Error(parsed.Error, parsed.ErrorDescription!);
            parameters = parsed.Parameters!;
            fromObject = true;
        }

        if (parameters.ClientId is not null && parameters.ClientId != app.ClientId)
            return Error("invalid_request", "client_id does not match the authenticated client.");
        if (string.IsNullOrEmpty(parameters.RedirectUri) || !app.RedirectUris.Contains(parameters.RedirectUri, StringComparer.Ordinal))
            return Error("invalid_request", "Invalid redirect_uri.");
        if (parameters.ResponseType != "code")
            return Error("unsupported_response_type", "Only 'code' response type is supported.");
        if (app.RequireSignedRequestObject && !fromObject)
            return Error("invalid_request", "This client must send a signed request object.");

        var requestUri = await requestObjects.PushAsync(session, app, parameters, fromObject, ct);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { request_uri = requestUri, expires_in = (int)OidcRequestObjects.PushedRequestLifetime.TotalSeconds },
            statusCode: StatusCodes.Status201Created);
    }

    private static IResult Error(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);
}
