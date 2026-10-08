using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;

// The provider's own password form and its two-factor step. Membership of
// OidcLocalCredentialsGroup is what gates them on SparkLocalCredentials - see that group's IsEnabled.
// The login page and its submit are OidcLoginPage and OidcLoginSubmit<TUser> (Endpoints/Login.cs).
//
// ⚠️ The antiforgery stamp on the submits is explicit. These pages read the form body with
// ReadFormAsync rather than [FromForm], so minimal APIs never inferred the metadata for them and the
// pages went unprotected.

/// <summary>Renders the two-factor step.</summary>
[MemberOf<OidcLocalCredentialsGroup>]
internal sealed class OidcTwoFactorPage : IGetEndpoint
{
    public static string Path => "/two-factor";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await TwoFactor.HandleGet(httpContext);
        return Results.Empty;
    }
}

/// <summary>Accepts the two-factor step.</summary>
[MemberOf<OidcLocalCredentialsGroup>]
internal sealed class OidcTwoFactorSubmit : IPostEndpoint
{
    public static string Path => "/two-factor";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await TwoFactor.HandlePost(httpContext);
        return Results.Empty;
    }
}
