using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Endpoints;
using MintPlayer.Spark.Authorization.Services;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// Extension methods on <see cref="ISparkBuilder"/> for registering OIDC external login providers.
/// </summary>
public static class SparkBuilderOidcExtensions
{
    /// <summary>
    /// Registers an OIDC external login provider with Spark.
    /// Can be called multiple times to register multiple providers.
    /// <example>
    /// <code>
    /// builder.AddAuthentication&lt;AppUser&gt;()
    ///     .AddOidcLogin("google", options =&gt;
    ///     {
    ///         options.Authority = "https://accounts.google.com";
    ///         options.ClientId = "your-client-id";
    ///         options.ClientSecret = "your-client-secret";
    ///         options.DisplayName = "Google";
    ///         options.Icon = "google";
    ///     })
    ///     .AddOidcLogin("microsoft", options =&gt;
    ///     {
    ///         options.Authority = "https://login.microsoftonline.com/common/v2.0";
    ///         options.ClientId = "your-client-id";
    ///         options.DisplayName = "Microsoft";
    ///         options.Icon = "microsoft";
    ///     });
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="builder">The Spark builder.</param>
    /// <param name="scheme">Unique scheme name for this provider (e.g., "google", "microsoft").</param>
    /// <param name="configure">Action to configure the OIDC provider options.</param>
    /// <returns>The Spark builder for chaining.</returns>
    public static ISparkBuilder AddOidcLogin(
        this ISparkBuilder builder,
        string scheme,
        Action<SparkOidcLoginOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SparkOidcLoginOptions();
        configure(options);

        // Validate required fields
        if (string.IsNullOrEmpty(options.ClientId))
        {
            throw new InvalidOperationException(
                $"ClientId is required for OIDC login provider '{scheme}'.");
        }

        if (string.IsNullOrEmpty(options.Authority) &&
            string.IsNullOrEmpty(options.AuthorizationEndpoint))
        {
            throw new InvalidOperationException(
                $"Either Authority or AuthorizationEndpoint must be configured for OIDC login provider '{scheme}'.");
        }

        if (string.IsNullOrEmpty(options.DisplayName))
        {
            options.DisplayName = scheme;
        }

        // Register shared OIDC services (idempotent via TryAdd)
        builder.Services.TryAddSingleton<OidcProviderRegistry>();
        builder.Services.TryAddSingleton<OidcDiscoveryService>();
        builder.Services.TryAddSingleton<OidcClientService>();
        builder.Services.AddHttpClient();
        builder.Services.AddDataProtection();

        // Register this provider configuration; picked up by OidcProviderRegistry via DI
        builder.Services.AddSingleton<IConfigureOidcProvider>(new ConfigureOidcProvider(scheme, options));

        // Register OIDC endpoints (once, detected via marker service in DI)
        if (!builder.Services.Any(d => d.ServiceType == typeof(OidcEndpointsMarker)))
        {
            builder.Services.AddSingleton<OidcEndpointsMarker>();
            builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkOidcEndpoints());
        }

        return builder;
    }

    /// <summary>
    /// Maps the OIDC-related endpoints under /spark/auth/.
    /// </summary>
    internal static IEndpointRouteBuilder MapSparkOidcEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var authGroup = endpoints.MapGroup("/spark/auth");

        authGroup.MapGet("/external-providers", ExternalProviders.Handle);
        authGroup.MapGet("/external-login/{scheme}", (Delegate)ExternalLogin.Handle);
        authGroup.MapGet("/oidc-callback", (Delegate)OidcCallback.Handle);
        authGroup.MapGet("/logins", (Delegate)ExternalLogins.HandleList).RequireAuthorization();
        authGroup.MapDelete("/logins/{provider}", (Delegate)ExternalLogins.HandleRemove).RequireAuthorization();

        return endpoints;
    }
}

/// <summary>
/// Marker class used to detect whether OIDC endpoints have already been registered in DI.
/// </summary>
internal sealed class OidcEndpointsMarker;

