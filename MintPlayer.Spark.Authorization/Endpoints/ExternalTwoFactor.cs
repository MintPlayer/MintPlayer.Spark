using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints;

/// <summary>
/// MVC-style two-factor verification page for external login flows.
/// When an external login callback detects that the user has 2FA enabled,
/// it stores user state in an encrypted cookie and redirects here.
/// GET renders the code entry form, POST verifies and completes sign-in.
/// </summary>
internal static class ExternalTwoFactor
{
    internal const string CookieName = ".SparkAuth.Pending2fa";
    internal const string ProtectorPurpose = "SparkAuth.Pending2fa.v1";

    public static async Task<IResult> HandleGet(
        HttpContext httpContext,
        IDataProtectionProvider dataProtectionProvider)
    {
        if (!TryGetPendingCookie(httpContext, dataProtectionProvider, out _))
        {
            return Results.BadRequest("No pending two-factor authentication.");
        }

        var error = httpContext.Request.Query["error"].FirstOrDefault();
        var useRecoveryCode = httpContext.Request.Query["recovery"].FirstOrDefault() == "true";

        return Results.Content(BuildFormHtml(error, useRecoveryCode), "text/html; charset=utf-8");
    }

    public static async Task<IResult> HandlePost(
        HttpContext httpContext,
        IDataProtectionProvider dataProtectionProvider,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MintPlayer.Spark.Authorization.Endpoints.ExternalTwoFactor");

        if (!TryGetPendingCookie(httpContext, dataProtectionProvider, out var pending))
        {
            return Results.BadRequest("No pending two-factor authentication.");
        }

        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var code = form["code"].FirstOrDefault()?.Replace(" ", "").Replace("-", "");
        var recoveryCode = form["recoveryCode"].FirstOrDefault()?.Replace(" ", "");
        var useRecoveryCode = form["useRecoveryCode"].FirstOrDefault() == "true";

        if (useRecoveryCode && string.IsNullOrEmpty(recoveryCode))
        {
            return Results.Content(BuildFormHtml("Please enter a recovery code.", useRecoveryCode: true), "text/html; charset=utf-8");
        }

        if (!useRecoveryCode && string.IsNullOrEmpty(code))
        {
            return Results.Content(BuildFormHtml("Please enter your authentication code."), "text/html; charset=utf-8");
        }

        // Resolve UserManager dynamically
        var sparkRegistry = serviceProvider.GetRequiredService<SparkModuleRegistry>();
        var userType = sparkRegistry.IdentityUserType
            ?? throw new InvalidOperationException("No identity user type configured.");

        var userManagerType = typeof(UserManager<>).MakeGenericType(userType);
        var signInManagerType = typeof(SignInManager<>).MakeGenericType(userType);
        var userManager = serviceProvider.GetRequiredService(userManagerType);
        var signInManager = serviceProvider.GetRequiredService(signInManagerType);

        // Find user by ID
        var findByIdMethod = userManagerType.GetMethod("FindByIdAsync")!;
        var user = await (dynamic)findByIdMethod.Invoke(userManager, [pending.UserId])!;

        if (user == null)
        {
            logger.LogWarning("Pending 2FA user {UserId} not found", pending.UserId);
            return Results.Content(BuildFormHtml("User not found. Please try logging in again."), "text/html; charset=utf-8");
        }

        bool verified;
        if (useRecoveryCode)
        {
            var redeemMethod = userManagerType.GetMethod("RedeemTwoFactorRecoveryCodeAsync")!;
            var redeemResult = (IdentityResult)await (dynamic)redeemMethod.Invoke(userManager, [user, recoveryCode])!;
            verified = redeemResult.Succeeded;

            if (verified)
                logger.LogInformation("2FA recovery code verified for user {UserId}", pending.UserId);
            else
                logger.LogWarning("Invalid 2FA recovery code for user {UserId}", pending.UserId);
        }
        else
        {
            var identityOptions = serviceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;
            var tokenProvider = identityOptions.Tokens.AuthenticatorTokenProvider;

            var verifyMethod = userManagerType.GetMethod("VerifyTwoFactorTokenAsync")!;
            verified = (bool)await (dynamic)verifyMethod.Invoke(userManager, [user, tokenProvider, code])!;

            if (verified)
                logger.LogInformation("2FA authenticator code verified for user {UserId}", pending.UserId);
            else
                logger.LogWarning("Invalid 2FA authenticator code for user {UserId}", pending.UserId);
        }

        if (!verified)
        {
            var errorMsg = useRecoveryCode ? "Invalid recovery code." : "Invalid authentication code.";
            return Results.Content(BuildFormHtml(errorMsg, useRecoveryCode), "text/html; charset=utf-8");
        }

        // 2FA verified — sign in the user
        var signInMethod = signInManagerType.GetMethod("SignInAsync",
            [signInManagerType.GetGenericArguments()[0], typeof(bool), typeof(string)])!;
        await (dynamic)signInMethod.Invoke(signInManager, [user, true, null])!;

        // Clear pending 2FA cookie
        httpContext.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/spark/auth" });

        if (pending.Popup)
        {
            return PopupSuccessResult(httpContext, pending.Scheme);
        }

        return Results.Redirect(pending.ReturnUrl);
    }

    internal static void StorePendingCookie(
        HttpContext httpContext,
        IDataProtectionProvider dataProtectionProvider,
        Pending2faCookie cookie)
    {
        var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        var json = JsonSerializer.Serialize(cookie);
        var encrypted = protector.Protect(json);

        httpContext.Response.Cookies.Append(CookieName, encrypted, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(5),
            Path = "/spark/auth",
        });
    }

    private static bool TryGetPendingCookie(
        HttpContext httpContext,
        IDataProtectionProvider dataProtectionProvider,
        out Pending2faCookie cookie)
    {
        cookie = default!;
        var encrypted = httpContext.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(encrypted)) return false;

        try
        {
            var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            var decrypted = protector.Unprotect(encrypted);
            cookie = JsonSerializer.Deserialize<Pending2faCookie>(decrypted)!;
            return cookie != null;
        }
        catch
        {
            return false;
        }
    }

    private static IResult PopupSuccessResult(HttpContext httpContext, string scheme)
    {
        var request = httpContext.Request;
        var origin = $"{request.Scheme}://{request.Host}";

        var messageProps = $@"""status"":""success"",""scheme"":""{JsEncode(scheme)}""";

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

        return Results.Content(html, "text/html");
    }

    private static string BuildFormHtml(string? error = null, bool useRecoveryCode = false)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><title>Two-Factor Authentication</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;max-width:400px;margin:80px auto;padding:0 20px}");
        sb.Append("h2{color:#333;margin-bottom:24px}");
        sb.Append(".form-group{margin-bottom:16px}");
        sb.Append("label{display:block;margin-bottom:4px;font-weight:500;font-size:14px}");
        sb.Append("input[type=text]{width:100%;padding:8px 12px;border:1px solid #ced4da;border-radius:6px;font-size:14px;box-sizing:border-box}");
        sb.Append("input[type=text]:focus{border-color:#86b7fe;outline:0;box-shadow:0 0 0 .25rem rgba(13,110,253,.25)}");
        sb.Append(".btn{display:block;width:100%;padding:10px;border:none;border-radius:6px;font-size:14px;cursor:pointer;box-sizing:border-box}");
        sb.Append(".btn-primary{background:#0d6efd;color:white;margin-top:8px}");
        sb.Append(".btn-primary:hover{background:#0b5ed7}");
        sb.Append(".btn-link{background:none;border:none;color:#0d6efd;cursor:pointer;padding:0;font-size:14px;text-decoration:underline;margin-top:12px;display:inline-block}");
        sb.Append(".error{color:#dc3545;background:#f8d7da;border:1px solid #f5c2c7;padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append(".info{color:#084298;background:#cfe2ff;border:1px solid #b6d4fe;padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append("</style></head><body>");
        sb.Append("<h2>Two-Factor Authentication</h2>");

        if (!string.IsNullOrEmpty(error))
        {
            sb.Append("<div class=\"error\">").Append(HtmlEncode(error)).Append("</div>");
        }

        sb.Append("<form method=\"post\">");

        if (useRecoveryCode)
        {
            sb.Append("<div class=\"info\">Enter one of your recovery codes.</div>");
            sb.Append("<input type=\"hidden\" name=\"useRecoveryCode\" value=\"true\" />");
            sb.Append("<div class=\"form-group\">");
            sb.Append("<label for=\"recoveryCode\">Recovery Code</label>");
            sb.Append("<input type=\"text\" id=\"recoveryCode\" name=\"recoveryCode\" required autofocus autocomplete=\"off\" />");
            sb.Append("</div>");
            sb.Append("<button type=\"submit\" class=\"btn btn-primary\">Verify</button>");
            sb.Append("</form>");
            sb.Append("<a href=\"?\" class=\"btn-link\">Use authenticator code instead</a>");
        }
        else
        {
            sb.Append("<div class=\"info\">Enter the 6-digit code from your authenticator app.</div>");
            sb.Append("<div class=\"form-group\">");
            sb.Append("<label for=\"code\">Authentication Code</label>");
            sb.Append("<input type=\"text\" id=\"code\" name=\"code\" required autofocus autocomplete=\"one-time-code\" inputmode=\"numeric\" pattern=\"[0-9]*\" maxlength=\"6\" />");
            sb.Append("</div>");
            sb.Append("<button type=\"submit\" class=\"btn btn-primary\">Verify</button>");
            sb.Append("</form>");
            sb.Append("<a href=\"?recovery=true\" class=\"btn-link\">Use a recovery code instead</a>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string HtmlEncode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private static string JsEncode(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");
}

/// <summary>
/// Data stored in the encrypted pending 2FA cookie during the external login flow.
/// Created by OidcCallback when a user with 2FA enabled is found.
/// Consumed by ExternalTwoFactor after the user enters their code.
/// </summary>
internal class Pending2faCookie
{
    public string UserId { get; set; } = "";
    public string Scheme { get; set; } = "";
    public bool Popup { get; set; }
    public string ReturnUrl { get; set; } = "/";
}
