using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class Logout
{
    public static async Task Handle(HttpContext context)
    {
        var ct = context.RequestAborted;
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
            // Validate post_logout_redirect_uri against registered client URIs
            var store = context.RequestServices.GetRequiredService<IDocumentStore>();
            using var session = store.OpenAsyncSession();

            var apps = await session.Query<OidcApplication, OidcApplications_ByClientId>()
                .Where(a => a.Enabled)
                .ToListAsync(ct);

            var isValid = apps.Any(a =>
                a.PostLogoutRedirectUris.Contains(postLogoutRedirectUri, StringComparer.Ordinal));

            if (!isValid)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<html><body><h2>Invalid post_logout_redirect_uri</h2><p>The provided redirect URI is not registered.</p></body></html>");
                return;
            }

            var redirectUrl = postLogoutRedirectUri;
            if (!string.IsNullOrEmpty(state))
            {
                redirectUrl += (redirectUrl.Contains('?') ? "&" : "?") + $"state={Uri.EscapeDataString(state)}";
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
