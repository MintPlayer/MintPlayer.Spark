using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>The device authorization request's form (RFC 8628 §3.1).</summary>
internal sealed record OidcDeviceAuthorizationRequest(string? Scope);

/// <summary>
/// <c>POST /connect/device_authorization</c> (RFC 8628 §3.1): a device with no browser, or no keyboard,
/// gets a device code to poll with and a short user code the person types at <c>/connect/device</c>.
/// </summary>
[MemberOf<OidcConnectCorsGroup>]
internal sealed partial class OidcDeviceAuthorization : IPostEndpoint<OidcDeviceAuthorizationRequest>
{
    public static string Path => "/device_authorization";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcClientAuthenticator clientAuthenticator;
    [Inject] private readonly OidcIssuer oidcIssuer;

    private HttpContext context = null!;
    private IFormCollection form = null!;

    protected override async ValueTask<OidcDeviceAuthorizationRequest?> BindRequestAsync(HttpContext context)
    {
        this.context = context;
        if (!context.Request.HasFormContentType)
            throw new EndpointBindingException(StatusCodes.Status400BadRequest, "Content-Type must be application/x-www-form-urlencoded.");
        form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcDeviceAuthorizationRequest(form["scope"].FirstOrDefault());
    }

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.Json(new { error = "invalid_request", error_description = failure?.Message }, statusCode: StatusCodes.Status400BadRequest));

    public override async Task<IResult> HandleAsync(OidcDeviceAuthorizationRequest request, CancellationToken ct)
    {
        using var session = store.OpenAsyncSession();
        var client = await clientAuthenticator.AuthenticateAsync(context, form, session, ct);
        if (!client.Succeeded)
            return client.ToResult(context);
        var app = client.Application!;

        if (!app.AllowedGrantTypes.Contains(OidcDeviceCodes.GrantType, StringComparer.Ordinal))
            return Error("unauthorized_client", "This client is not authorized for the device authorization grant.");

        var scopes = (request.Scope ?? "openid").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var defined = await OidcScopeCatalog.LoadAsync(session, scopes, ct);
        foreach (var s in scopes)
        {
            if (!app.ScopeNames().Contains(s, StringComparer.OrdinalIgnoreCase) || !defined.Any(d => string.Equals(d.Name, s, StringComparison.OrdinalIgnoreCase)))
                return Error("invalid_scope", $"Scope '{s}' is not allowed for this client.");
        }

        var deviceCode = OidcTokenReference.GenerateValue();
        var userCode = OidcDeviceCodes.NewUserCode();
        var expiresAt = DateTime.UtcNow.Add(OidcDeviceCodes.Lifetime);
        var device = new OidcToken
        {
            Id = OidcDeviceCodes.DeviceDocumentId(deviceCode),
            Type = OidcTokenTypes.DeviceCode,
            ApplicationId = app.Id!,
            Scopes = scopes,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            Properties = { ["user_code"] = OidcDeviceCodes.Normalize(userCode) },
        };
        await session.StoreExpiringAsync(device, ct);
        await session.StoreExpiringAsync(new OidcToken
        {
            Id = OidcDeviceCodes.UserCodeDocumentId(userCode),
            Type = OidcTokenTypes.DeviceCode,
            ApplicationId = app.Id!,
            Status = "pointer",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            Properties = { ["device"] = device.Id },
        }, ct);
        await session.SaveChangesAsync(ct);

        var verificationUri = $"{oidcIssuer.Resolve(context.Request)}/connect/device";
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new
        {
            device_code = deviceCode,
            user_code = userCode,
            verification_uri = verificationUri,
            verification_uri_complete = $"{verificationUri}?user_code={Uri.EscapeDataString(userCode)}",
            expires_in = (int)OidcDeviceCodes.Lifetime.TotalSeconds,
            interval = OidcDeviceCodes.IntervalSeconds,
        });
    }

    private static IResult Error(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);
}
