using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;
using MintPlayer.Spark.Authorization.Endpoints.Passkeys;
using MintPlayer.Spark.Authorization.Extensions;

namespace MintPlayer.Spark.Authorization.Endpoints;

/// <summary>
/// Tells the client which sign-in methods this application actually offers: how much of the
/// local-credential surface is mounted, and which external providers are registered.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous by design — it describes exactly what an unauthenticated visitor is about to be shown
/// on a sign-in page, so it discloses nothing that page would not.
/// </para>
/// <para>
/// Every flag is <em>derived from the mapped endpoints</em>, asked by endpoint type
/// (<c>IsEndpointMapped</c>; <see cref="SparkIdentityEndpoints"/> for routes without a class), rather
/// than read back from the options object. Reporting the configured value would let this endpoint claim a surface that was
/// never mapped (or deny one that was); deriving it from the endpoints that exist means the answer
/// is true by construction, and stays true if the mapping is ever reached by some other path.
/// </para>
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed class GetAuthCapabilities : IGetEndpoint
{
    public static string Path => "/capabilities";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var endpoints = services.GetRequiredService<EndpointDataSource>();
        // Login (Microsoft's) and register (Spark's) carry SparkIdentityEndpoints stand-in types, so
        // every flag here is asked by endpoint type and no route string can silently flip one.
        var localCredentials =
            !endpoints.IsEndpointMapped<SparkIdentityEndpoints.Login>() ? SparkLocalCredentials.Disabled
            : !endpoints.IsEndpointMapped<SparkIdentityEndpoints.Register>() ? SparkLocalCredentials.SignInOnly
            : SparkLocalCredentials.Full;

        // The 2FA page needs both the settings endpoint and the authenticator payload.
        var twoFactor = endpoints.IsEndpointMapped<SparkIdentityEndpoints.TwoFactor>()
            && endpoints.IsEndpointMapped<SparkIdentityEndpoints.AuthenticatorUri>();

        // Derived, for the same reason as the mode above: the sign-in page renders a passkey button
        // from this flag, and a page offering a ceremony whose endpoint was never mapped is a dead
        // button. Keyed on the sign-in endpoint rather than the enrollment one — this answers "can an
        // anonymous visitor sign in with a passkey", which is the question the sign-in page asks.
        // Asked by endpoint class, so a renamed route or prefix cannot silently flip it.
        var passkeys = endpoints.IsEndpointMapped(typeof(PasskeySignIn<>));

        // The option, AND the route that carries the change: POST manage/info is mapped only outside
        // LocalCredentials Disabled, so the option alone would offer a form that posts into a 404.
        var emailChange = SparkAccountEndpoints.EmailChangeEnabled(services)
            && localCredentials != SparkLocalCredentials.Disabled;

        // Derived like passkeys: the connected-logins page exists only when ExternalLoginLinking mapped
        // its endpoints, and an app with external sign-in but no linking must not link to it.
        var externalLogins = endpoints.IsEndpointMapped(typeof(ListExternalLogins<>));

        var providers = await ExternalAuthenticationSchemes.GetInteractiveAsync(services);

        return Results.Ok(new
        {
            localCredentials = localCredentials.ToString(),
            passkeys,
            twoFactor,
            emailChange,
            externalLogins,
            externalProviders = providers
                .Select(scheme => new { scheme = scheme.Name, displayName = scheme.DisplayName })
                .ToArray(),
        });
    }
}
