using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// Refuses to start with an external-login scheme nobody declared to Spark (#464/#490 Q4b).
/// </summary>
/// <remarks>
/// <para>
/// A remote handler (anything deriving from <see cref="RemoteAuthenticationHandler{TOptions}"/>: OAuth,
/// OpenID Connect, Google, …) registered through <c>services.AddAuthentication()</c> is offered on the
/// sign-in page like any preset, but nothing says whether its email can be trusted, and it reports no
/// provider-side failure back to the popup. Spark's presets declare both; a raw handler is declared with
/// <c>spark.AddExternalScheme(scheme, policy)</c>. An explicit
/// <see cref="SparkAuthenticationOptions.ExternalProviders"/> entry also counts as a declaration.
/// </para>
/// <para>
/// Runs where the other authentication guards run (<c>LocalCredentialEndpointFilter</c>): when the
/// endpoints are mapped, the first moment the full scheme table is known.
/// </para>
/// </remarks>
internal static class SparkExternalSchemeGuard
{
    internal static void GuardAgainstUndeclaredRemoteSchemes(IServiceProvider services)
    {
        if (services.GetService(typeof(IAuthenticationSchemeProvider)) is not IAuthenticationSchemeProvider provider)
            return;

        // Case-insensitive, as the policy lookup matches registrations (SparkExternalProviderPolicies.For).
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in services.GetServices<SparkExternalProviderRegistration>())
            declared.Add(registration.Scheme);
        if (services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value is { } options)
            declared.UnionWith(options.ExternalProviders.Keys);

        var undeclared = provider.GetAllSchemesAsync().GetAwaiter().GetResult()
            .Where(scheme => IsRemote(scheme.HandlerType) && !declared.Contains(scheme.Name))
            .ToArray();
        if (undeclared.Length == 0)
            return;

        throw new InvalidOperationException(
            $"External-login scheme(s) {string.Join(", ", undeclared.Select(s => $"'{s.Name}' ({s.HandlerType.Name})"))} "
            + "are registered without a Spark declaration, so nothing says whether their email can be trusted "
            + "on a first sign-in. Use a Spark preset (spark.AddGitHub(), spark.AddGoogle(), spark.AddOpenIdConnect(), "
            + "spark.AddExternalProviders(configuration), …), or declare a handler you register yourself with "
            + "spark.AddExternalScheme(\"<scheme>\", policy).");
    }

    /// <summary>Whether <paramref name="handlerType"/> derives from <see cref="RemoteAuthenticationHandler{TOptions}"/>.</summary>
    internal static bool IsRemote(Type? handlerType)
    {
        for (var type = handlerType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RemoteAuthenticationHandler<>))
                return true;
        }
        return false;
    }
}
