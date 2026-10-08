using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>GET /spark/auth/manage/2fa/authenticator-uri</c>: the authenticator's shared key, its
/// <c>otpauth://</c> URI and a server-rendered QR code. Every mode.
/// </summary>
/// <remarks>
/// Read-only: the key is created by <c>POST /manage/2fa</c> (Microsoft's), never by a GET. The 2FA page
/// needs both, which is why <c>GetAuthCapabilities</c> asks for this class.
/// </remarks>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class AuthenticatorUri<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/2fa/authenticator-uri";

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;
    [Inject] private readonly SparkQrCodeRenderer qrCodeRenderer;
    [Inject] private readonly IHostEnvironment? environment;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (await userManager.GetUserAsync(httpContext.User) is not { } user)
            return Results.NotFound();

        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
            return Results.Json(new { error = "no_authenticator_key" }, statusCode: StatusCodes.Status409Conflict);

        var issuer = options.Value.AuthenticatorIssuer
            ?? environment?.ApplicationName
            ?? "Spark";
        var account = user.Email ?? user.UserName ?? user.Id ?? "account";

        var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
            + $"?secret={Uri.EscapeDataString(key)}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

        // The body is the shared secret: never cached anywhere.
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new SparkAuthenticatorUriResponse(key, uri, qrCodeRenderer.RenderSvg(uri)));
    }
}
