using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class Logout
{
    public static async Task Handle(HttpContext context)
    {
        var query = context.Request.Query;
        var postLogoutRedirectUri = query["post_logout_redirect_uri"].FirstOrDefault();
        var state = query["state"].FirstOrDefault();

        // Sign out the user if authenticated
        var registry = context.RequestServices.GetRequiredService<SparkModuleRegistry>();
        var userType = registry.IdentityUserType;

        if (userType != null && context.User?.Identity?.IsAuthenticated == true)
        {
            var signInManagerType = typeof(SignInManager<>).MakeGenericType(userType);
            var signInManager = context.RequestServices.GetRequiredService(signInManagerType);

            var signOutMethod = signInManagerType.GetMethod("SignOutAsync")!;
            await (Task)signOutMethod.Invoke(signInManager, [])!;
        }

        if (!string.IsNullOrEmpty(postLogoutRedirectUri))
        {
            // TODO: validate post_logout_redirect_uri against registered client URIs
            var redirectUrl = postLogoutRedirectUri;
            if (!string.IsNullOrEmpty(state))
            {
                redirectUrl += $"?state={Uri.EscapeDataString(state)}";
            }
            context.Response.Redirect(redirectUrl);
        }
        else
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body><h2>You have been signed out.</h2><p>You may close this window.</p></body></html>");
        }
    }
}
