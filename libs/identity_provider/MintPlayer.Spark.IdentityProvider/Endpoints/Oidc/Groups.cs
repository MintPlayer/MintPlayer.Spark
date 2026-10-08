using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Extensions;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;

/// <summary>
/// The discovery surface. Both members carry the dynamic-CORS convention.
/// </summary>
internal class OidcWellKnownGroup : IEndpointGroup
{
    public static string Prefix => "/.well-known";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) => OidcCors.Apply(group, services);
}

/// <summary>
/// The protocol surface, unconditional and without the CORS convention.
/// </summary>
internal class OidcConnectGroup : IEndpointGroup
{
    public static string Prefix => "/connect";
}

/// <summary>
/// The provider's own password form and its two-factor step.
/// </summary>
/// <remarks>
/// It honours the application's <see cref="SparkLocalCredentials"/> mode for the same reason
/// <c>/spark/auth/login</c> does — and because it would otherwise be a way to keep a password
/// surface alive in an application that had turned local credentials off. The protocol endpoints in
/// the sibling groups are untouched: an identity provider that federates to an upstream provider
/// still needs every one of them.
/// <para>
/// This is Spark's first use of <c>IsEnabled</c>, and the fit is exact — <c>LocalCredentialsOf</c>
/// already takes an <see cref="IServiceProvider"/>, and <c>IsEnabled</c> is evaluated once at map
/// time against the root provider, the same moment and the same provider as the <c>if</c> this
/// replaces.
/// </para>
/// </remarks>
internal class OidcLocalCredentialsGroup : IEndpointGroup
{
    public static string Prefix => "/connect";

    static bool IEndpointGroup.IsEnabled(IServiceProvider services)
        => SparkIdentityProviderExtensions.LocalCredentialsOf(services) != SparkLocalCredentials.Disabled;
}

/// <summary>
/// The protocol endpoints that carry the dynamic-CORS convention.
/// </summary>
/// <remarks>
/// A separate group rather than a per-endpoint convention: one <c>Configure</c> covers every member.
/// Note <c>/connect/introspect</c> is deliberately
/// <em>not</em> a member: it never carried <c>WithOidcCors</c>.
/// <para>
/// Its members are also the machine endpoints (token, PAR, device authorization, revocation, userinfo), so the
/// group carries the identity provider's named rate-limit policy (PRD D9) as well.
/// </para>
/// </remarks>
internal class OidcConnectCorsGroup : IEndpointGroup
{
    public static string Prefix => "/connect";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
    {
        OidcCors.Apply(group, services);
        group.RequireRateLimiting(SparkIdentityProviderExtensions.RateLimitPolicy);
    }
}

/// <summary>
/// Applies the dynamic-CORS policy to a group when the option is set.
/// </summary>
/// <remarks>
/// ⚠️ <c>IsEnabled</c> cannot express this: it would <em>unmap</em> <c>/connect/token</c> when CORS
/// is off, which is the opposite of the requirement. <c>Configure</c> can: it receives the
/// application's root provider, evaluated once when the routes are mapped.
/// </remarks>
internal static class OidcCors
{
    public static void Apply(RouteGroupBuilder group, IServiceProvider services)
    {
        if (services.GetRequiredService<SparkIdentityProviderOptions>().EnableDynamicCors)
            group.RequireCors(SparkIdentityProviderExtensions.CorsPolicy);
    }
}
