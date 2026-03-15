using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// MVC two-factor authentication page for the OIDC Identity Provider login flow.
/// GET renders the code entry form, POST verifies and redirects to returnUrl.
/// </summary>
internal static class TwoFactor
{
    public static async Task HandleGet(HttpContext context)
    {
        var returnUrl = context.Request.Query["returnUrl"].FirstOrDefault() ?? "/";
        var error = context.Request.Query["error"].FirstOrDefault();
        var useRecoveryCode = context.Request.Query["recovery"].FirstOrDefault() == "true";

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(BuildFormHtml(returnUrl, error, useRecoveryCode));
    }

    public static async Task HandlePost(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var code = form["code"].FirstOrDefault()?.Replace(" ", "").Replace("-", "");
        var recoveryCode = form["recoveryCode"].FirstOrDefault()?.Replace(" ", "");
        var useRecoveryCode = form["useRecoveryCode"].FirstOrDefault() == "true";
        var returnUrl = form["returnUrl"].FirstOrDefault() ?? "/";

        if (useRecoveryCode && string.IsNullOrEmpty(recoveryCode))
        {
            RedirectWithError(context, returnUrl, "Please enter a recovery code.", recovery: true);
            return;
        }

        if (!useRecoveryCode && string.IsNullOrEmpty(code))
        {
            RedirectWithError(context, returnUrl, "Please enter your authentication code.");
            return;
        }

        // Resolve SignInManager dynamically
        var registry = context.RequestServices.GetRequiredService<SparkModuleRegistry>();
        var userType = registry.IdentityUserType;
        if (userType == null)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Identity not configured.");
            return;
        }

        var signInManagerType = typeof(SignInManager<>).MakeGenericType(userType);
        var signInManager = context.RequestServices.GetRequiredService(signInManagerType);

        if (useRecoveryCode)
        {
            // TwoFactorRecoveryCodeSignInAsync(recoveryCode)
            var method = signInManagerType.GetMethod("TwoFactorRecoveryCodeSignInAsync")!;
            var result = (SignInResult)await (dynamic)method.Invoke(signInManager, [recoveryCode])!;

            if (result.Succeeded)
            {
                context.Response.Redirect(returnUrl);
                return;
            }

            RedirectWithError(context, returnUrl, "Invalid recovery code.", recovery: true);
        }
        else
        {
            // TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient)
            var method = signInManagerType.GetMethod("TwoFactorAuthenticatorSignInAsync")!;
            var result = (SignInResult)await (dynamic)method.Invoke(signInManager, [code, true, false])!;

            if (result.Succeeded)
            {
                context.Response.Redirect(returnUrl);
                return;
            }

            RedirectWithError(context, returnUrl, "Invalid authentication code.");
        }
    }

    private static string BuildFormHtml(string returnUrl, string? error, bool useRecoveryCode)
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
            sb.Append("<div class=\"error\">").Append(Encode(error)).Append("</div>");
        }

        sb.Append("<form method=\"post\">");
        sb.Append("<input type=\"hidden\" name=\"returnUrl\" value=\"").Append(Encode(returnUrl)).Append("\" />");

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
            sb.Append("<a href=\"/connect/two-factor?returnUrl=").Append(Uri.EscapeDataString(returnUrl)).Append("\" class=\"btn-link\">Use authenticator code instead</a>");
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
            sb.Append("<a href=\"/connect/two-factor?returnUrl=").Append(Uri.EscapeDataString(returnUrl)).Append("&recovery=true\" class=\"btn-link\">Use a recovery code instead</a>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void RedirectWithError(HttpContext context, string returnUrl, string error, bool recovery = false)
    {
        var url = $"/connect/two-factor?returnUrl={Uri.EscapeDataString(returnUrl)}&error={Uri.EscapeDataString(error)}";
        if (recovery) url += "&recovery=true";
        context.Response.Redirect(url);
    }

    private static string Encode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}
