using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Extensions;

public static class SparkAuthenticationExtensions
{
    /// <summary>
    /// Adds Spark authentication with RavenDB-backed identity stores.
    /// Registers ASP.NET Core Identity services, bearer + cookie authentication,
    /// and the RavenDB user/role stores.
    /// <example>
    /// <code>
    /// // Basic usage:
    /// builder.Services.AddSparkAuthentication&lt;AppUser&gt;();
    ///
    /// // With identity options:
    /// builder.Services.AddSparkAuthentication&lt;AppUser&gt;(options =&gt;
    /// {
    ///     options.Password.RequireDigit = true;
    ///     options.Lockout.MaxFailedAccessAttempts = 5;
    /// });
    ///
    /// // With external login providers:
    /// builder.Services
    ///     .AddSparkAuthentication&lt;AppUser&gt;()
    ///     .AddGoogle(o =&gt; builder.Configuration.GetSection("Authentication:Google").Bind(o))
    ///     .AddMicrosoftAccount(o =&gt; builder.Configuration.GetSection("Authentication:Microsoft").Bind(o));
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureIdentity">Optional callback to configure identity options (password rules, lockout, etc.).</param>
    /// <typeparam name="TUser">The user type, must extend <see cref="SparkUser"/>.</typeparam>
    /// <returns>The <see cref="IdentityBuilder"/> for further configuration (e.g. adding external login providers).</returns>
    public static IdentityBuilder AddSparkAuthentication<TUser>(
        this IServiceCollection services,
        Action<IdentityOptions>? configureIdentity = null)
        where TUser : SparkUser, new()
    {
        var builder = services
            .AddIdentityApiEndpoints<TUser>(options =>
            {
                configureIdentity?.Invoke(options);
            })
            .AddRoles<SparkRole>();

        builder.Services.AddScoped<IUserStore<TUser>, UserStore<TUser>>();
        builder.Services.AddScoped<IRoleStore<SparkRole>, RoleStore>();

        return builder;
    }

    /// <summary>
    /// Maps the ASP.NET Core Identity API endpoints for Spark authentication.
    /// Provides: POST /register, POST /login, POST /refresh, GET /confirmEmail,
    /// POST /forgotPassword, POST /resetPassword, POST /manage/2fa, GET /manage/info, POST /manage/info.
    /// </summary>
    public static IEndpointRouteBuilder MapSparkIdentityApi<TUser>(
        this IEndpointRouteBuilder endpoints)
        where TUser : SparkUser, new()
    {
        endpoints.MapIdentityApi<TUser>();
        return endpoints;
    }
}
