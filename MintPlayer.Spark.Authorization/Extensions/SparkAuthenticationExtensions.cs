using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Identity;
using System.Security.Claims;

namespace MintPlayer.Spark.Authorization.Extensions;

internal static class SparkAuthenticationExtensions
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
    internal static IdentityBuilder AddSparkAuthentication<TUser>(
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

        services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");

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
    internal static IEndpointRouteBuilder MapSparkIdentityApi<TUser>(
        this IEndpointRouteBuilder endpoints)
        where TUser : SparkUser, new()
    {
        var authGroup = endpoints.MapGroup("/spark/auth");
        authGroup.MapIdentityApi<TUser>();

        // Map Spark auth endpoints (source-generated)
        endpoints.MapSparkAuthEndpoints();

        // External login: initiate OAuth challenge
        authGroup.MapGet("/external-login", (
            HttpContext context,
            SignInManager<TUser> signInManager,
            string provider,
            string? returnUrl) =>
        {
            var callbackUrl = $"/spark/auth/external-login-callback?returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}";
            var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, callbackUrl);
            return Results.Challenge(properties, [provider]);
        });

        // External login: handle OAuth callback — mapped on root endpoints (not authGroup)
        // to avoid any group-level auth configuration from MapIdentityApi
        endpoints.MapGet("/spark/auth/external-login-callback", async (
            HttpContext context,
            SignInManager<TUser> signInManager,
            UserManager<TUser> userManager,
            IAntiforgery antiforgery,
            string? returnUrl) =>
        {
            // Debug: try authenticating with the external scheme directly
            var authResult = await context.AuthenticateAsync(IdentityConstants.ExternalScheme);
            var debugInfo = $"ExternalScheme authenticated: {authResult.Succeeded}, " +
                            $"Principal null: {authResult.Principal is null}, " +
                            $"Properties null: {authResult.Properties is null}, " +
                            $"Items: {(authResult.Properties?.Items is not null ? string.Join(", ", authResult.Properties.Items.Select(kv => $"{kv.Key}={kv.Value}")) : "null")}, " +
                            $"Cookies: {string.Join(", ", context.Request.Cookies.Select(c => c.Key))}";

            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
                return Results.Content($"<html><body><h1>External login info is null</h1><pre>{debugInfo}</pre></body></html>", "text/html");

            // Try signing in with existing external login
            var result = await signInManager.ExternalLoginSignInAsync(
                info.LoginProvider, info.ProviderKey, isPersistent: true);

            TUser? user;
            if (result.Succeeded)
            {
                user = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            }
            else
            {
                // First-time login: create user from external claims
                var email = info.Principal.FindFirstValue(ClaimTypes.Email);
                var userName = info.Principal.FindFirstValue(ClaimTypes.Name)
                    ?? info.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

                user = new TUser();
                await userManager.SetUserNameAsync(user, userName);
                await userManager.SetEmailAsync(user, email);

                var createResult = await userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                    return Results.Redirect(returnUrl ?? "/");

                await userManager.AddLoginAsync(user, info);
                await signInManager.SignInAsync(user, isPersistent: true);
            }

            // Store OAuth tokens for later API use
            if (user is not null && info.AuthenticationTokens is not null)
            {
                foreach (var token in info.AuthenticationTokens)
                {
                    await userManager.SetAuthenticationTokenAsync(
                        user, info.LoginProvider, token.Name, token.Value);
                }
            }

            // Ensure antiforgery cookie is set before redirect
            antiforgery.GetAndStoreTokens(context);

            // Return an HTML page that handles popup (postMessage) and non-popup (redirect) flows
            var safeReturnUrl = returnUrl ?? "/";
            var html = $$"""
                <!DOCTYPE html>
                <html><head><title>Signing in...</title></head>
                <body>
                <script>
                if (window.opener) {
                    window.opener.postMessage({ type: 'external-login-success' }, window.location.origin);
                    window.close();
                } else {
                    window.location.href = '{{safeReturnUrl}}';
                }
                </script>
                </body></html>
                """;
            return Results.Content(html, "text/html");
        }).AllowAnonymous();

        return endpoints;
    }
}
