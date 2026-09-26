using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;

// The provider's own password form and its two-factor step. Membership of
// OidcLocalCredentialsGroup is what gates them on SparkLocalCredentials - see that group's IsEnabled.

/// <summary>Renders the provider's password form.</summary>
[MemberOf<OidcLocalCredentialsGroup>]
internal sealed class OidcLoginPage : IGetEndpoint
{
    public static string Path => "/login";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Login.HandleGet(httpContext);
        return Results.Empty;
    }
}

/// <summary>Accepts the provider's password form.</summary>
/// <remarks>
/// ⚠️ The antiforgery stamp is explicit. These pages read the form body with <c>ReadFormAsync</c>
/// rather than <c>[FromForm]</c>, so minimal APIs never inferred the metadata for them and the pages
/// went unprotected.
/// </remarks>
[MemberOf<OidcLocalCredentialsGroup>]
internal sealed class OidcLoginSubmit : IPostEndpoint
{
    public static string Path => "/login";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await Login.HandlePost(httpContext);
        return Results.Empty;
    }
}

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

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        await TwoFactor.HandlePost(httpContext);
        return Results.Empty;
    }
}
