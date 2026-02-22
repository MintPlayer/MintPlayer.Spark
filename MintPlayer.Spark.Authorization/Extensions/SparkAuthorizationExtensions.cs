using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization;

/// <summary>
/// Extension methods for configuring Spark authorization services.
/// </summary>
public static class SparkAuthorizationExtensions
{
    /// <summary>
    /// Adds Spark authorization services to the service collection.
    /// When these services are registered, Spark will check permissions
    /// before allowing CRUD operations on PersistentObjects and Queries.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureOptions">Optional configuration for authorization options</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddSparkAuthorization(options =>
    /// {
    ///     options.SecurityFilePath = "App_Data/security.json";
    ///     options.DefaultBehavior = DefaultAccessBehavior.DenyAll;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddSparkAuthorization(
        this IServiceCollection services,
        Action<AuthorizationOptions>? configureOptions = null)
    {
        var options = new AuthorizationOptions();
        configureOptions?.Invoke(options);

        services.Configure<AuthorizationOptions>(opt =>
        {
            opt.SecurityFilePath = options.SecurityFilePath;
            opt.DefaultBehavior = options.DefaultBehavior;
            opt.CacheRights = options.CacheRights;
            opt.CacheExpirationMinutes = options.CacheExpirationMinutes;
            opt.EnableHotReload = options.EnableHotReload;
        });

        // Ensure HttpContextAccessor is available
        services.AddHttpContextAccessor();

        // Register core services
        services.AddSparkAuthorizationServices();

        return services;
    }

    /// <summary>
    /// Replaces the default group membership provider with a custom implementation.
    /// Use this to integrate with your own authentication system (ASP.NET Identity, OAuth, etc.).
    /// </summary>
    /// <typeparam name="TProvider">The custom provider type implementing IGroupMembershipProvider</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddSparkAuthorization()
    ///     .AddGroupMembershipProvider&lt;IdentityGroupMembershipProvider&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddGroupMembershipProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IGroupMembershipProvider
    {
        // Remove any existing registration
        var existingDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IGroupMembershipProvider));
        if (existingDescriptor != null)
        {
            services.Remove(existingDescriptor);
        }

        services.AddScoped<IGroupMembershipProvider, TProvider>();
        return services;
    }
}
