using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Extensions;

public static class SparkBuilderAuthorizationExtensions
{
    /// <summary>
    /// Adds Spark authorization services (group-based access control).
    /// </summary>
    public static ISparkBuilder AddAuthorization(
        this ISparkBuilder builder,
        Action<AuthorizationOptions>? configureOptions = null)
    {
        builder.Services.AddSparkAuthorization(configureOptions);
        return builder;
    }

    /// <summary>
    /// Adds Spark authentication with RavenDB-backed identity stores.
    /// Registers ASP.NET Core Identity services, bearer + cookie authentication,
    /// and the RavenDB user/role stores.
    /// </summary>
    public static ISparkBuilder AddAuthentication<TUser>(
        this ISparkBuilder builder,
        Action<IdentityOptions>? configureIdentity = null,
        Action<IdentityBuilder>? configureProviders = null)
        where TUser : SparkUser, new()
    {
        builder.Registry.IdentityUserType = typeof(TUser);

        var identityBuilder = builder.Services.AddSparkAuthentication<TUser>(configureIdentity);
        configureProviders?.Invoke(identityBuilder);

        // Register middleware and endpoint callbacks
        builder.Registry.AddEndpoints(endpoints =>
            endpoints.MapSparkIdentityApi<TUser>());

        return builder;
    }
}
