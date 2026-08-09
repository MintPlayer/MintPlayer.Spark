using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Abstractions.Authentication;

/// <summary>
/// Registers the ways a caller may prove who it is.
/// <para>
/// Each credential type otherwise needs its own bespoke wiring, and — because Spark's endpoints
/// name no scheme — would silently never run. These extensions put every credential behind one
/// composite default scheme, so adding a certificate or a bearer token is a registration rather
/// than a parallel authorization path.
/// </para>
/// </summary>
public static class SparkCredentialSchemeExtensions
{
    /// <summary>
    /// Declares that an already-registered authentication scheme participates in authenticating
    /// Spark requests. Use this when the scheme is registered by its own package's extension
    /// (<c>AddJwtBearer</c>, <c>AddCertificate</c>, Identity's cookie and bearer schemes).
    /// </summary>
    /// <param name="isAmbient">
    /// <c>true</c> only for a credential the browser attaches on its own — a cookie. That is what
    /// makes CSRF possible, and therefore what makes an antiforgery token worth demanding. See
    /// <see cref="SparkCredentialScheme"/>.
    /// </param>
    public static ISparkBuilder AddCredentialScheme(
        this ISparkBuilder builder,
        string scheme,
        bool isAmbient = false)
    {
        builder.Registry.AddCredentialScheme(scheme, isAmbient);
        builder.EnsureCompositeScheme();
        return builder;
    }

    /// <summary>
    /// Registers <typeparamref name="THandler"/> under <paramref name="scheme"/> and declares it a
    /// Spark credential in one step.
    /// </summary>
    public static ISparkBuilder AddCredentialScheme<TOptions, THandler>(
        this ISparkBuilder builder,
        string scheme,
        Action<TOptions>? configureOptions = null,
        bool isAmbient = false)
        where TOptions : AuthenticationSchemeOptions, new()
        where THandler : AuthenticationHandler<TOptions>
    {
        builder.Services
            .AddAuthentication()
            .AddScheme<TOptions, THandler>(scheme, configureOptions);

        return builder.AddCredentialScheme(scheme, isAmbient);
    }

    /// <summary>
    /// Installs the composite handler and makes it the default authenticate scheme.
    /// <para>
    /// Only <c>DefaultAuthenticateScheme</c> is overridden. Challenge, sign-in and sign-out stay
    /// wherever Identity put them — the composite reads credentials and issues none, so pointing
    /// a sign-in at it would have nothing to write to.
    /// </para>
    /// <para>
    /// Idempotent: called by every <c>AddCredentialScheme</c> overload, since any one of them may
    /// be the first.
    /// </para>
    /// </summary>
    private static void EnsureCompositeScheme(this ISparkBuilder builder)
    {
        var marker = typeof(SparkCompositeAuthenticationHandler);
        if (builder.Services.Any(d => d.ServiceType == marker))
            return;

        builder.Services.AddSingleton(marker, marker);

        builder.Services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, SparkCompositeAuthenticationHandler>(
                SparkAuthenticationDefaults.CompositeScheme, _ => { });

        // PostConfigure, not Configure: Identity sets the default scheme from inside
        // AddIdentityApiEndpoints, and whichever configuration callback runs last wins. Ordering
        // this after all of them is what makes the composite the default regardless of the order
        // an app happens to call AddAuthentication and AddCredentialScheme in.
        builder.Services.PostConfigure<AuthenticationOptions>(options =>
            options.DefaultAuthenticateScheme = SparkAuthenticationDefaults.CompositeScheme);

        // The registry is what the handler reads its scheme list from. AddSpark registers it too;
        // TryAdd keeps whichever instance the builder is actually populating.
        builder.Services.TryAddSingleton(builder.Registry);
    }
}
