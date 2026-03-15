using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.Services;

namespace MintPlayer.Spark.Authorization.Endpoints;

/// <summary>
/// GET /spark/auth/oidc-callback?code=...&amp;state=...
/// Handles the redirect from the external OIDC provider after user authentication.
/// </summary>
internal static class OidcCallback
{
    private const string CookieName = ".SparkAuth.OidcState";
    private const string ProtectorPurpose = "SparkAuth.OidcState.v1";

    public static async Task<IResult> Handle(
        HttpContext httpContext,
        OidcClientService oidcClientService,
        OidcProviderRegistry registry,
        IDataProtectionProvider dataProtectionProvider,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MintPlayer.Spark.Authorization.Endpoints.OidcCallback");
        var ct = httpContext.RequestAborted;

        // Read query parameters
        var code = httpContext.Request.Query["code"].FirstOrDefault();
        var stateParam = httpContext.Request.Query["state"].FirstOrDefault();
        var error = httpContext.Request.Query["error"].FirstOrDefault();

        if (!string.IsNullOrEmpty(error))
        {
            var errorDescription = httpContext.Request.Query["error_description"].FirstOrDefault();
            logger.LogWarning("OIDC callback received error: {Error} - {Description}", error, errorDescription);

            // Check if this was a popup flow (peek at state cookie)
            if (TryGetStateCookie(httpContext, dataProtectionProvider, out var errStateCookie) && errStateCookie.Popup)
            {
                return PopupResult(httpContext, "error", errStateCookie.Scheme, error, errorDescription);
            }

            return Results.Redirect($"/?error=external_login_failed&description={Uri.EscapeDataString(errorDescription ?? error)}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(stateParam))
        {
            return Results.BadRequest(new { error = "Missing code or state parameter." });
        }

        // Read and decrypt the state cookie
        var encryptedState = httpContext.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(encryptedState))
        {
            return Results.BadRequest(new { error = "Missing OIDC state cookie. The login flow may have expired." });
        }

        OidcStateCookie stateCookie;
        try
        {
            var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            var decrypted = protector.Unprotect(encryptedState);
            stateCookie = JsonSerializer.Deserialize<OidcStateCookie>(decrypted)
                ?? throw new InvalidOperationException("Failed to deserialize state cookie.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to decrypt OIDC state cookie");
            return Results.BadRequest(new { error = "Invalid or expired state cookie." });
        }

        // Validate state matches
        if (!string.Equals(stateCookie.State, stateParam, StringComparison.Ordinal))
        {
            logger.LogWarning("OIDC state mismatch. Expected: {Expected}, Got: {Got}", stateCookie.State, stateParam);
            return Results.BadRequest(new { error = "State parameter mismatch." });
        }

        // Build redirect URI
        var request = httpContext.Request;
        var redirectUri = $"{request.Scheme}://{request.Host}/spark/auth/oidc-callback";

        // Exchange authorization code for tokens
        var tokenResponse = await oidcClientService.ExchangeCodeAsync(
            stateCookie.Scheme, code, redirectUri, stateCookie.CodeVerifier, ct);

        if (tokenResponse?.AccessToken == null)
        {
            logger.LogWarning("Failed to exchange authorization code for tokens (scheme: {Scheme})", stateCookie.Scheme);

            if (stateCookie.Popup)
            {
                return PopupResult(httpContext, "error", stateCookie.Scheme, "token_exchange_failed");
            }

            return Results.Redirect($"{stateCookie.ReturnUrl}?error=token_exchange_failed");
        }

        // Extract user info from ID token claims and/or userinfo endpoint
        var claims = new Dictionary<string, string>();

        // Try parsing ID token claims from JWT payload (header.payload.signature)
        if (!string.IsNullOrEmpty(tokenResponse.IdToken))
        {
            try
            {
                var parts = tokenResponse.IdToken.Split('.');
                if (parts.Length >= 2)
                {
                    var payload = Base64UrlDecode(parts[1]);
                    var payloadJson = Encoding.UTF8.GetString(payload);
                    var payloadClaims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);

                    if (payloadClaims != null)
                    {
                        foreach (var (key, value) in payloadClaims)
                        {
                            claims.TryAdd(key, value.ValueKind == JsonValueKind.String
                                ? value.GetString() ?? ""
                                : value.GetRawText());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse ID token, will fall back to userinfo endpoint");
            }
        }

        // Fetch from userinfo endpoint if we need more claims
        if (!claims.ContainsKey("sub") || !claims.ContainsKey("email"))
        {
            var userInfo = await oidcClientService.GetUserInfoAsync(
                stateCookie.Scheme, tokenResponse.AccessToken, ct);

            if (userInfo != null)
            {
                foreach (var (key, value) in userInfo)
                {
                    claims.TryAdd(key, value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? ""
                        : value.GetRawText());
                }
            }
        }

        // Extract key claims with fallbacks
        var providerKey = claims.GetValueOrDefault("sub") ?? claims.GetValueOrDefault("id") ?? "";
        var email = claims.GetValueOrDefault("email");
        var name = claims.GetValueOrDefault("name")
            ?? claims.GetValueOrDefault("preferred_username")
            ?? email;

        if (string.IsNullOrEmpty(providerKey))
        {
            logger.LogWarning("No provider key (sub/id) found in claims from scheme {Scheme}", stateCookie.Scheme);
            return Results.Redirect($"{stateCookie.ReturnUrl}?error=no_provider_key");
        }

        // Apply claim mappings from configuration
        var providerRegistration = registry.GetByScheme(stateCookie.Scheme);
        if (providerRegistration != null)
        {
            foreach (var (externalClaim, localClaim) in providerRegistration.Options.ClaimMappings)
            {
                if (claims.TryGetValue(externalClaim, out var value))
                {
                    claims[localClaim] = value;
                }
            }
        }

        // Resolve UserManager and SignInManager dynamically based on configured user type
        var sparkRegistry = serviceProvider.GetRequiredService<SparkModuleRegistry>();
        var userType = sparkRegistry.IdentityUserType
            ?? throw new InvalidOperationException("No identity user type configured. Call AddAuthentication<TUser>() before AddOidcLogin().");

        // Find or create user through UserManager/SignInManager
        var result = await FindOrCreateUserAndSignInAsync(
            httpContext, serviceProvider, userType, stateCookie.Scheme,
            providerKey, email, name, logger, ct);

        if (result.Status == ExternalSignInStatus.Failed)
        {
            if (stateCookie.Popup)
            {
                return PopupResult(httpContext, "error", stateCookie.Scheme, "login_failed");
            }

            return Results.Redirect($"{stateCookie.ReturnUrl}?error=login_failed");
        }

        // Clear OIDC state cookie (OAuth flow is complete regardless of 2FA)
        httpContext.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            Path = "/spark/auth",
        });

        if (result.Status == ExternalSignInStatus.RequiresTwoFactor)
        {
            // Store pending 2FA state and redirect to inline 2FA form
            ExternalTwoFactor.StorePendingCookie(httpContext, dataProtectionProvider, new Pending2faCookie
            {
                UserId = result.UserId!,
                Scheme = stateCookie.Scheme,
                Popup = stateCookie.Popup,
                ReturnUrl = stateCookie.ReturnUrl,
            });
            return Results.Redirect("/spark/auth/external-two-factor");
        }

        if (stateCookie.Popup)
        {
            return PopupResult(httpContext, "success", stateCookie.Scheme);
        }

        // Redirect to return URL
        return Results.Redirect(stateCookie.ReturnUrl);
    }

    private static async Task<ExternalSignInResult> FindOrCreateUserAndSignInAsync(
        HttpContext httpContext,
        IServiceProvider serviceProvider,
        Type userType,
        string scheme,
        string providerKey,
        string? email,
        string? name,
        ILogger logger,
        CancellationToken ct)
    {
        // Resolve UserManager<TUser> and SignInManager<TUser> dynamically
        var userManagerType = typeof(UserManager<>).MakeGenericType(userType);
        var signInManagerType = typeof(SignInManager<>).MakeGenericType(userType);

        var userManager = serviceProvider.GetRequiredService(userManagerType);
        var signInManager = serviceProvider.GetRequiredService(signInManagerType);

        // 1. Try FindByLoginAsync(scheme, providerKey)
        var findByLoginMethod = userManagerType.GetMethod("FindByLoginAsync")!;
        var existingUser = await (dynamic)findByLoginMethod.Invoke(userManager, [scheme, providerKey])!;

        if (existingUser != null)
        {
            logger.LogInformation("Found existing user by external login ({Scheme}/{ProviderKey})", scheme, providerKey);
            if (((SparkUser)existingUser).TwoFactorEnabled)
            {
                return new ExternalSignInResult(ExternalSignInStatus.RequiresTwoFactor, ((SparkUser)existingUser).Id);
            }
            await SignInUserAsync(signInManager, signInManagerType, existingUser, httpContext);
            return new ExternalSignInResult(ExternalSignInStatus.Success);
        }

        // 2. Try FindByEmailAsync(email) and link
        if (!string.IsNullOrEmpty(email))
        {
            var findByEmailMethod = userManagerType.GetMethod("FindByEmailAsync")!;
            var userByEmail = await (dynamic)findByEmailMethod.Invoke(userManager, [email])!;

            if (userByEmail != null)
            {
                logger.LogInformation("Found existing user by email ({Email}), linking external login ({Scheme})", email, scheme);
                var loginInfo = new UserLoginInfo(scheme, providerKey, scheme);
                var addLoginMethod = userManagerType.GetMethod("AddLoginAsync")!;
                var addResult = await (dynamic)addLoginMethod.Invoke(userManager, [userByEmail, loginInfo])!;

                if (((IdentityResult)addResult).Succeeded)
                {
                    if (((SparkUser)userByEmail).TwoFactorEnabled)
                    {
                        return new ExternalSignInResult(ExternalSignInStatus.RequiresTwoFactor, ((SparkUser)userByEmail).Id);
                    }
                    await SignInUserAsync(signInManager, signInManagerType, userByEmail, httpContext);
                    return new ExternalSignInResult(ExternalSignInStatus.Success);
                }

                logger.LogWarning("Failed to link external login to existing user: {Errors}",
                    string.Join(", ", ((IdentityResult)addResult).Errors.Select(e => e.Description)));
                return new ExternalSignInResult(ExternalSignInStatus.Failed);
            }
        }

        // 3. Create new user with external login
        logger.LogInformation("Creating new user for external login ({Scheme}/{ProviderKey})", scheme, providerKey);

        var newUser = (SparkUser)Activator.CreateInstance(userType)!;
        newUser.UserName = name ?? email ?? $"{scheme}_{providerKey}";
        newUser.Email = email;
        newUser.EmailConfirmed = !string.IsNullOrEmpty(email); // Trust provider's email

        var createMethod = userManagerType.GetMethod("CreateAsync", [userType])!;
        var createResult = await (dynamic)createMethod.Invoke(userManager, [newUser])!;

        if (!((IdentityResult)createResult).Succeeded)
        {
            logger.LogWarning("Failed to create user: {Errors}",
                string.Join(", ", ((IdentityResult)createResult).Errors.Select(e => e.Description)));
            return new ExternalSignInResult(ExternalSignInStatus.Failed);
        }

        var loginInfoForNew = new UserLoginInfo(scheme, providerKey, scheme);
        var addLoginForNewMethod = userManagerType.GetMethod("AddLoginAsync")!;
        var addLoginResult = await (dynamic)addLoginForNewMethod.Invoke(userManager, [newUser, loginInfoForNew])!;

        if (!((IdentityResult)addLoginResult).Succeeded)
        {
            logger.LogWarning("Failed to add external login to new user: {Errors}",
                string.Join(", ", ((IdentityResult)addLoginResult).Errors.Select(e => e.Description)));
            return new ExternalSignInResult(ExternalSignInStatus.Failed);
        }

        await SignInUserAsync(signInManager, signInManagerType, newUser, httpContext);
        return new ExternalSignInResult(ExternalSignInStatus.Success);
    }

    private static async Task SignInUserAsync(
        object signInManager,
        Type signInManagerType,
        object user,
        HttpContext httpContext)
    {
        // Use SignInAsync(user, isPersistent: true)
        var signInMethod = signInManagerType.GetMethod("SignInAsync",
            [signInManagerType.GetGenericArguments()[0], typeof(bool), typeof(string)])!;
        await (dynamic)signInMethod.Invoke(signInManager, [user, true, null])!;
    }

    private static bool TryGetStateCookie(
        HttpContext httpContext,
        IDataProtectionProvider dataProtectionProvider,
        out OidcStateCookie cookie)
    {
        cookie = default!;
        var encrypted = httpContext.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(encrypted)) return false;

        try
        {
            var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            var decrypted = protector.Unprotect(encrypted);
            cookie = JsonSerializer.Deserialize<OidcStateCookie>(decrypted)!;
            return cookie != null;
        }
        catch
        {
            return false;
        }
    }

    private static IResult PopupResult(
        HttpContext httpContext,
        string status,
        string scheme,
        string? error = null,
        string? errorDescription = null)
    {
        var request = httpContext.Request;
        var origin = $"{request.Scheme}://{request.Host}";

        var messageProps = $@"""status"":""{Encode(status)}"",""scheme"":""{Encode(scheme)}""";
        if (error != null)
            messageProps += $@",""error"":""{Encode(error)}""";
        if (errorDescription != null)
            messageProps += $@",""errorDescription"":""{Encode(errorDescription)}""";

        var html = $$"""
            <!DOCTYPE html>
            <html><head><title>Login complete</title>
            <script>
                window.opener.postMessage('{{{messageProps}}}', '{{origin}}');
                window.close();
            </script>
            </head>
            <body><p>Login complete. This window will close automatically.</p></body>
            </html>
            """;

        // Clear state cookie
        httpContext.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/spark/auth" });

        return Results.Content(html, "text/html");
    }

    private static string Encode(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");

    /// <summary>
    /// Decodes a base64url-encoded string to bytes.
    /// Handles the padding that JWT omits.
    /// </summary>
    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private enum ExternalSignInStatus { Success, Failed, RequiresTwoFactor }
    private record ExternalSignInResult(ExternalSignInStatus Status, string? UserId = null);
}
