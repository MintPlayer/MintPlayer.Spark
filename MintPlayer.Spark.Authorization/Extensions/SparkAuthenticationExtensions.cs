using Microsoft.AspNetCore.Antiforgery;
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
            // R2-M3: validate returnUrl at the entry point. Even if R2-C4's
            // callback fix were bypassed, accepting an absolute attacker URL here
            // round-trips through OAuth state and reflects back to the callback
            // unchanged. Substitute the default for anything non-local.
            var safeReturnUrl = SanitizeReturnUrl(returnUrl);
            var callbackUrl = $"/spark/auth/external-login-callback?returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
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
            // R2-C4: returnUrl is interpolated into the HTML/JS response body below,
            // so anything other than a relative in-app path is a vector for XSS
            // (and at minimum open-redirect after successful OAuth). Sanitize first.
            var safeReturnUrl = SanitizeReturnUrl(returnUrl);

            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
                return Results.Redirect(safeReturnUrl);

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
                // R2-H11: only auto-provision when the issuer attested the email.
                // GitHub: 'urn:github:email_verified' is set by GitHubAuthenticationExtensions
                // when /user/emails reports primary && verified. Google/Microsoft/Apple:
                // standard "email_verified" claim. Refuse to bind an unverified email
                // to a fresh account — otherwise an attacker claiming any email at an
                // external IdP can squat that identity locally.
                var email = info.Principal.FindFirstValue(ClaimTypes.Email);
                var emailVerified = string.Equals(
                        info.Principal.FindFirstValue("email_verified"), "true",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        info.Principal.FindFirstValue("urn:github:email_verified"), "true",
                        StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrEmpty(email) || !emailVerified)
                    return Results.Redirect(safeReturnUrl);

                var userName = info.Principal.FindFirstValue(ClaimTypes.Name)
                    ?? info.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

                user = new TUser();
                await userManager.SetUserNameAsync(user, userName);
                await userManager.SetEmailAsync(user, email);
                // Issuer attested the email — mark it confirmed so the local account
                // is in the same state as a password-flow user who confirmed their address.
                user.EmailConfirmed = true;

                var createResult = await userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                    return Results.Redirect(safeReturnUrl);

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

            // R2-C4: Server-side redirect instead of HTML interpolation. The previous
            // implementation built an HTML page with returnUrl as a raw JS string
            // literal — even after SanitizeReturnUrl removes the worst-case XSS
            // payloads, the popup branch (window.opener.postMessage) is unaffected
            // (no caller-data interpolation), and the non-popup branch is now a
            // standard server redirect that the framework HTML-encodes for us.
            // Popup callers detect success via the explicit JSON content type
            // rather than a hard navigation.
            if (context.Request.Query.ContainsKey("popup"))
            {
                var popupHtml = """
                    <!DOCTYPE html>
                    <html><head><title>Signing in...</title></head>
                    <body>
                    <script>
                    if (window.opener) {
                        window.opener.postMessage({ type: 'external-login-success' }, window.location.origin);
                        window.close();
                    }
                    </script>
                    </body></html>
                    """;
                return Results.Content(popupHtml, "text/html");
            }
            return Results.Redirect(safeReturnUrl);
        }).AllowAnonymous();

        return endpoints;
    }

    /// <summary>
    /// R2-C4 / R2-M3 shared validator. Accepts only local relative URLs:
    /// must start with '/', must not start with '//' or '/\' (protocol-relative
    /// or backslash-confused-as-slash navigation), must not contain CR/LF
    /// (header-splitting). Returns "/" for anything else — never throws so
    /// callers can use it unconditionally.
    /// </summary>
    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)) return "/";
        if (returnUrl.IndexOfAny(['\r', '\n']) >= 0) return "/";
        if (!returnUrl.StartsWith('/')) return "/";
        if (returnUrl.Length >= 2 && (returnUrl[1] == '/' || returnUrl[1] == '\\')) return "/";
        return returnUrl;
    }
}
