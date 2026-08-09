using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
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
    /// Replaces how Spark decides which groups the current caller belongs to — the input to every
    /// <c>security.json</c> decision. The default reads <c>group</c>/<c>groups</c> claims; supply
    /// your own to resolve them from Identity roles, a directory, or a database.
    /// </summary>
    /// <remarks>
    /// This exists because the interface was a documented extension point with no public way to
    /// register an implementation — the replacement helper was <c>internal</c>, so consumers could
    /// only reach it by knowing that a later <c>AddScoped</c> happens to win. Removing the previous
    /// registration is the part worth owning here: leaving both in the container makes which
    /// provider runs depend on registration order.
    /// </remarks>
    public static ISparkBuilder UseGroupMembershipProvider<TProvider>(this ISparkBuilder builder)
        where TProvider : class, IGroupMembershipProvider
    {
        builder.Services.AddGroupMembershipProvider<TProvider>();
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

        // Identity's own two schemes, declared separately rather than as the combined
        // BearerAndApplication scheme. The combination would authenticate a request without saying
        // which half did it, and the antiforgery gate needs exactly that: the cookie is ambient and
        // must be defended against CSRF, the bearer is not and must not be obstructed by it.
        builder.AddCredentialScheme(IdentityConstants.ApplicationScheme, isAmbient: true);
        builder.AddCredentialScheme(IdentityConstants.BearerScheme);

        // Register middleware and endpoint callbacks
        builder.Registry.AddEndpoints(endpoints =>
            endpoints.MapSparkIdentityApi<TUser>());

        return builder;
    }
}
