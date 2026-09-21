using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Extensions;

public static class SparkBuilderAuthorizationExtensions
{
    /// <summary>
    /// Adds Spark authentication with RavenDB-backed identity stores.
    /// Registers ASP.NET Core Identity services, bearer + cookie authentication,
    /// and the RavenDB user/role stores.
    /// </summary>
    /// <param name="configure">
    /// Configures Spark's own authentication surface — notably
    /// <see cref="SparkAuthenticationOptions.LocalCredentials"/>, which chooses how much of the
    /// email/password endpoint family to mount. Defaults to <see cref="SparkLocalCredentials.Full"/>,
    /// so an application that omits it keeps the endpoint set Spark has always mapped.
    /// </param>
    public static ISparkBuilder AddAuthentication<TUser>(
        this ISparkBuilder builder,
        Action<SparkAuthenticationOptions>? configure = null,
        Action<IdentityOptions>? configureIdentity = null,
        Action<IdentityBuilder>? configureProviders = null)
        where TUser : SparkUser, new()
    {
        var options = new SparkAuthenticationOptions();
        configure?.Invoke(options);

        builder.Registry.IdentityUserType = typeof(TUser);

        // Published so other Spark packages can honour the same mode instead of carrying their own
        // copy of the flag. MintPlayer.Spark.IdentityProvider serves a second local-credential
        // surface at /connect/login; two flags could disagree, and an application reporting that
        // local credentials are off while still serving a password form would be believed.
        builder.Services.AddSingleton(options);

        // ⚠️ And the same instance behind IOptions<>. Registering only the concrete type looks
        // harmless because IOptions<SparkAuthenticationOptions> still *resolves* — the options
        // infrastructure constructs a default one for any T. So a consumer asking for it gets a
        // fully-formed object with every setting at its default and no indication that it is not
        // the configured one. That silently disabled the ConfirmByEmail startup guard and would
        // have made every external-login linking decision read Disabled in production, while tests
        // that configured the mode through Configure<T> passed. Options.Create hands back the very
        // object above, so the two accessors cannot disagree.
        builder.Services.AddSingleton<IOptions<SparkAuthenticationOptions>>(Options.Create(options));

        var identityBuilder = builder.Services.AddSparkAuthentication<TUser>(configureIdentity);
        configureProviders?.Invoke(identityBuilder);

        // Identity's own two schemes, declared separately rather than as the combined
        // BearerAndApplication scheme. The combination would authenticate a request without saying
        // which half did it, and the antiforgery gate needs exactly that: the cookie is ambient and
        // must be defended against CSRF, the bearer is not and must not be obstructed by it.
        builder.AddCredentialScheme(IdentityConstants.ApplicationScheme, isAmbient: true);
        builder.AddCredentialScheme(IdentityConstants.BearerScheme);

        // Register middleware and endpoint callbacks
        builder.Registry.AddEndpoints(endpoints =>
            endpoints.MapSparkIdentityApi<TUser>(options.LocalCredentials));

        return builder;
    }

}
