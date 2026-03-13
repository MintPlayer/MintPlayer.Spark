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
        var returnUrl = context.Request.Query["returnUrl"].FirstOrDefault() ?? "/";
        var error = context.Request.Query["error"].FirstOrDefault();

        context.Response.ContentType = "text/html; charset=utf-8";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><title>Login</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;max-width:400px;margin:80px auto;padding:0 20px}");
        sb.Append("h2{color:#333;margin-bottom:24px}");
        sb.Append(".form-group{margin-bottom:16px}");
        sb.Append("label{display:block;margin-bottom:4px;font-weight:500;font-size:14px}");
        sb.Append("input[type=email],input[type=password]{width:100%;padding:8px 12px;border:1px solid #ced4da;border-radius:6px;font-size:14px;box-sizing:border-box}");
        sb.Append("input[type=email]:focus,input[type=password]:focus{border-color:#86b7fe;outline:0;box-shadow:0 0 0 .25rem rgba(13,110,253,.25)}");
        sb.Append(".btn{display:block;width:100%;padding:10px;border:none;border-radius:6px;font-size:14px;cursor:pointer;box-sizing:border-box}");
        sb.Append(".btn-primary{background:#0d6efd;color:white;margin-top:8px}");
        sb.Append(".btn-primary:hover{background:#0b5ed7}");
        sb.Append(".error{color:#dc3545;background:#f8d7da;border:1px solid #f5c2c7;padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append("</style></head><body>");
        sb.Append("<h2>Login</h2>");

        if (!string.IsNullOrEmpty(error))
        {
            sb.Append("<div class=\"error\">").Append(Encode(error)).Append("</div>");
        }

        sb.Append("<form method=\"post\">");
        sb.Append("<input type=\"hidden\" name=\"returnUrl\" value=\"").Append(Encode(returnUrl)).Append("\" />");
        sb.Append("<div class=\"form-group\">");
        sb.Append("<label for=\"email\">Email</label>");
        sb.Append("<input type=\"email\" id=\"email\" name=\"email\" required autofocus />");
        sb.Append("</div>");
        sb.Append("<div class=\"form-group\">");
        sb.Append("<label for=\"password\">Password</label>");
        sb.Append("<input type=\"password\" id=\"password\" name=\"password\" required />");
        sb.Append("</div>");
        sb.Append("<button type=\"submit\" class=\"btn btn-primary\">Login</button>");
        sb.Append("</form></body></html>");

        await context.Response.WriteAsync(sb.ToString());
    }

    public static async Task HandlePost(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var email = form["email"].FirstOrDefault();
        var password = form["password"].FirstOrDefault();
        var returnUrl = form["returnUrl"].FirstOrDefault() ?? "/";

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            RedirectWithError(context, returnUrl, "Email and password are required.");
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
        var userManagerType = typeof(UserManager<>).MakeGenericType(userType);
        var signInManager = context.RequestServices.GetRequiredService(signInManagerType);
        var userManager = context.RequestServices.GetRequiredService(userManagerType);

        // Find user by email
        var findByEmailMethod = userManagerType.GetMethod("FindByEmailAsync")!;
        var user = await (dynamic)findByEmailMethod.Invoke(userManager, [email])!;

        if (user == null)
        {
            RedirectWithError(context, returnUrl, "Invalid email or password.");
            return;
        }

        // Attempt password sign-in
        var passwordSignInMethod = signInManagerType.GetMethod("PasswordSignInAsync",
            [userType, typeof(string), typeof(bool), typeof(bool)])!;
        var result = (SignInResult)await (dynamic)passwordSignInMethod.Invoke(
            signInManager, [user, password, true, false])!;

        if (result.Succeeded)
        {
            context.Response.Redirect(returnUrl);
            return;
        }

        if (result.RequiresTwoFactor)
        {
            // TODO: MVC two-factor page
            RedirectWithError(context, returnUrl, "Two-factor authentication is not yet supported in this flow.");
            return;
        }

        if (result.IsLockedOut)
        {
            RedirectWithError(context, returnUrl, "Account is locked out. Please try again later.");
            return;
        }

        RedirectWithError(context, returnUrl, "Invalid email or password.");
    }

    private static void RedirectWithError(HttpContext context, string returnUrl, string error)
    {
        var loginUrl = $"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error={Uri.EscapeDataString(error)}";
        context.Response.Redirect(loginUrl);
    }

    private static string Encode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}
