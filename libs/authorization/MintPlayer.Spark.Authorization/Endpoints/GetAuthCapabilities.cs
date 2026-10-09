using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Endpoints.Account;
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
/// (<c>IsEndpointMapped</c>: the endpoint class, or <see cref="SparkIdentityEndpoints"/> for Microsoft's
/// routes, which have none), rather than read back from the options object. Reporting the configured
/// value would let this endpoint claim a surface that was never mapped (or deny one that was); deriving
/// it from the endpoints that exist means the answer is true by construction, and stays true if the
/// mapping is ever reached by some other path.
/// </para>
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class GetAuthCapabilities : IGetEndpoint
{
    public static string Path => "/capabilities";

    [Inject] private readonly EndpointDataSource endpoints;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;
    [Inject] private readonly IAuthenticationSchemeProvider? schemes;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        // Login is Microsoft's and carries a SparkIdentityEndpoints stand-in type; register is Spark's
        // own class. Every flag here is asked by endpoint type, so no route string can silently flip one.
        var localCredentials =
            !endpoints.IsEndpointMapped<SparkIdentityEndpoints.Login>() ? SparkLocalCredentials.Disabled
            : !endpoints.IsEndpointMapped(typeof(Register<>)) ? SparkLocalCredentials.SignInOnly
            : SparkLocalCredentials.Full;

        // The 2FA page needs both the settings endpoint and the authenticator payload.
        var twoFactor = endpoints.IsEndpointMapped<SparkIdentityEndpoints.TwoFactor>()
            && endpoints.IsEndpointMapped(typeof(AuthenticatorUri<>));

        // Derived, for the same reason as the mode above: the sign-in page renders a passkey button
        // from this flag, and a page offering a ceremony whose endpoint was never mapped is a dead
        // button. Keyed on the sign-in endpoint rather than the enrollment one — this answers "can an
        // anonymous visitor sign in with a passkey", which is the question the sign-in page asks.
        // Asked by endpoint class, so a renamed route or prefix cannot silently flip it.
        var passkeys = endpoints.IsEndpointMapped(typeof(PasskeySignIn<>));

        // The option, AND the route that carries the change: POST manage/info exists only when accounts
        // have passwords, so the option alone could offer a form that posts into a 404.
        var emailChange = SparkAccount.EmailChangeEnabled(options.Value)
            && endpoints.IsEndpointMapped(typeof(UpdateInfo<>));

        // Derived like passkeys: the connected-logins page exists only when ExternalLoginLinking mapped
        // its endpoints, and an app with external sign-in but no linking must not link to it.
        var externalLogins = endpoints.IsEndpointMapped(typeof(ListExternalLogins<>));

        // The one value read from the options rather than derived from a route: it shapes how /login
        // resolves an identifier, not which routes exist. Empty when there is no password sign-in.
        var identifiers = options.Value.SignInIdentifiers;
        var signInIdentifiers = new List<string>();
        if (localCredentials != SparkLocalCredentials.Disabled)
        {
            if (identifiers.HasFlag(SparkSignInIdentifiers.Email)) signInIdentifiers.Add("email");
            if (identifiers.HasFlag(SparkSignInIdentifiers.UserName)) signInIdentifiers.Add("userName");
        }

        // #490 D11: the account page offers the skip only where its endpoint is mapped (AllowUserBypass).
        var externalLoginTwoFactorBypass = endpoints.IsEndpointMapped(typeof(ExternalLoginTwoFactorBypassState<>));

        var providers = await ExternalAuthenticationSchemes.GetInteractiveAsync(schemes);

        return Results.Ok(new
        {
            localCredentials = localCredentials.ToString(),
            signInIdentifiers,
            passkeys,
            twoFactor,
            emailChange,
            externalLogins,
            externalLoginTwoFactorBypass,
            externalProviders = providers
                .Select(scheme => new { scheme = scheme.Name, displayName = scheme.DisplayName })
                .ToArray(),
        });
    }
}
