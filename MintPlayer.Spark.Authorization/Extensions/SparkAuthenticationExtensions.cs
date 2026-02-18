using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Endpoints;
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
    /// Maps the ASP.NET Core Identity API endpoints for Spark authentication
    /// under the <c>/spark/auth</c> route prefix.
    /// Provides: POST /spark/auth/register, POST /spark/auth/login, POST /spark/auth/refresh,
    /// GET /spark/auth/confirmEmail, POST /spark/auth/forgotPassword, POST /spark/auth/resetPassword,
    /// POST /spark/auth/manage/2fa, GET /spark/auth/manage/info, POST /spark/auth/manage/info,
    /// GET /spark/auth/me, POST /spark/auth/logout.
    /// </summary>
    public static IEndpointRouteBuilder MapSparkIdentityApi<TUser>(
        this IEndpointRouteBuilder endpoints)
        where TUser : SparkUser, new()
    {
        var authGroup = endpoints.MapGroup("/spark/auth");
        authGroup.MapIdentityApi<TUser>();
        authGroup.MapGet("/me", GetCurrentUser.Handle);
        authGroup.MapPost("/logout", (Delegate)Logout.Handle);
        return endpoints;
    }
}
