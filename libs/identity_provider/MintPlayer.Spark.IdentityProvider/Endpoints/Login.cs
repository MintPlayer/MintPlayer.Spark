using MintPlayer.Spark.Authorization.Extensions;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// MVC login page for the OIDC authorization flow.
/// GET renders an HTML login form, POST authenticates and redirects to returnUrl.
/// </summary>
internal static class Login
{
    public static async Task HandleGet(HttpContext context)
    {
        // Sanitized at the point of read: an unvalidated returnUrl sends a freshly-authenticated
        // user off-origin, which is high-value phishing precisely because they really did just
        // authenticate here. Shared with the Authorization package rather than duplicated.
        var returnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(context.Request.Query["returnUrl"].FirstOrDefault());
        var error = context.Request.Query["error"].FirstOrDefault();

        context.Response.ContentType = "text/html; charset=utf-8";
        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, context, "Login");
        sb.Append("body{max-width:400px;margin:80px auto;padding:0 20px}");
        sb.Append("h2{margin-bottom:24px}");
        sb.Append(".form-group{margin-bottom:16px}");
        sb.Append("label{display:block;margin-bottom:4px;font-weight:500;font-size:14px}");
        sb.Append("input[type=text],input[type=password]{width:100%;padding:8px 12px;border:1px solid var(--idp-input-border);border-radius:6px;font-size:14px;box-sizing:border-box}");
        sb.Append("input[type=text]:focus,input[type=password]:focus{border-color:var(--idp-focus-border);outline:0;box-shadow:0 0 0 .25rem var(--idp-focus-ring)}");
        sb.Append(".btn{display:block;width:100%;padding:10px;border:none;border-radius:6px;font-size:14px;cursor:pointer;box-sizing:border-box}");
        sb.Append(".btn-primary{background:var(--idp-primary);color:#fff;margin-top:8px}");
        sb.Append(".btn-primary:hover{background:var(--idp-primary-hover)}");
        sb.Append(".error{color:var(--idp-error-color);background:var(--idp-error-bg);border:1px solid var(--idp-error-border);padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append("</style></head><body>");
        sb.Append("<h2>Login</h2>");

        if (!string.IsNullOrEmpty(error))
        {
            sb.Append("<div class=\"error\">").Append(Encode(ConnectPage.ErrorMessage(error)!)).Append("</div>");
        }

        sb.Append("<form method=\"post\">");
        ConnectPage.AppendAntiforgery(sb, context);
        sb.Append("<input type=\"hidden\" name=\"returnUrl\" value=\"").Append(Encode(returnUrl)).Append("\" />");
        sb.Append("<div class=\"form-group\">");
        sb.Append("<label for=\"identifier\">Email or user name</label>");
        sb.Append("<input type=\"text\" id=\"identifier\" name=\"identifier\" autocomplete=\"username\" required autofocus />");
        sb.Append("</div>");
        sb.Append("<div class=\"form-group\">");
        sb.Append("<label for=\"password\">Password</label>");
        sb.Append("<input type=\"password\" id=\"password\" name=\"password\" required />");
        sb.Append("</div>");
        sb.Append("<div class=\"form-group\">");
        sb.Append("<label><input type=\"checkbox\" name=\"rememberMe\" value=\"true\" /> Remember me</label>");
        sb.Append("</div>");
        sb.Append("<button type=\"submit\" class=\"btn btn-primary\">Login</button>");
        sb.Append("</form></body></html>");

        await context.Response.WriteAsync(sb.ToString());
    }

    public static async Task HandlePost(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        // "email" is the field name older copies of this page post; "identifier" is the current one.
        var identifier = form["identifier"].FirstOrDefault() ?? form["email"].FirstOrDefault();
        var password = form["password"].FirstOrDefault();
        var returnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(form["returnUrl"].FirstOrDefault());
        var rememberMe = string.Equals(form["rememberMe"].FirstOrDefault(), "true", StringComparison.Ordinal);

        if (string.IsNullOrEmpty(identifier) || string.IsNullOrEmpty(password))
        {
            RedirectWithError(context, returnUrl, "missing_fields");
            return;
        }

        // Resolve the configured user type and SignInManager dynamically
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

        // Attempt password sign-in through the string overload, which SparkSignInManager overrides:
        // the identifier is an email or a user name, resolved by the same rule as /spark/auth/login
        // (#460, D4). An unknown identifier fails exactly like a wrong password.
        //
        // lockoutOnFailure was false, which meant failures never reached AccessFailedAsync: an
        // unauthenticated, unthrottled endpoint would test passwords forever, and the
        // IsLockedOut branch below was unreachable. MapIdentityApi's own login passes true, so
        // this page was strictly weaker than the API beside it.
        //
        // isPersistent was hardcoded true, silently issuing every visitor a persistent cookie.
        // It now follows the checkbox the user actually saw.
        var passwordSignInMethod = signInManagerType.GetMethod("PasswordSignInAsync",
            [typeof(string), typeof(string), typeof(bool), typeof(bool)])!;
        var result = (SignInResult)await (dynamic)passwordSignInMethod.Invoke(
            signInManager, [identifier, password, rememberMe, true])!;

        if (result.Succeeded)
        {
            context.Response.Redirect(returnUrl);
            return;
        }

        if (result.RequiresTwoFactor)
        {
            // The remember-me choice is made here and spent on the next hop, so it has to travel.
            var carry = rememberMe ? "&rememberMe=true" : "";
            context.Response.Redirect($"/connect/two-factor?returnUrl={Uri.EscapeDataString(returnUrl)}{carry}");
            return;
        }

        if (result.IsLockedOut)
        {
            RedirectWithError(context, returnUrl, "locked_out");
            return;
        }

        RedirectWithError(context, returnUrl, "invalid_credentials");
    }

    private static void RedirectWithError(HttpContext context, string returnUrl, string error)
    {
        var loginUrl = $"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error={Uri.EscapeDataString(error)}";
        context.Response.Redirect(loginUrl);
    }

    private static string Encode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}
