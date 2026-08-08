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

            // The URI must be registered by *the client asking*, which means the request has to
            // say who that is. Validating against every enabled application instead — as this
            // did — makes one client's registered URI a legal logout destination for every
            // other client, so anyone who can register an application gains a redirect through
            // this provider's origin for all of them. client_id is how RP-initiated logout
            // identifies the caller (OIDC RP-Initiated Logout 1.0 §2).
            var clientId = query["client_id"].FirstOrDefault();
            var app = string.IsNullOrEmpty(clientId)
                ? null
                : await Authorize.FindApplicationByClientIdAsync(session, clientId, ct);

            if (app is not { Enabled: true }
                || !app.PostLogoutRedirectUris.Contains(postLogoutRedirectUri, StringComparer.Ordinal))
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<html><body><h2>Invalid post_logout_redirect_uri</h2><p>The provided redirect URI is not registered for this client.</p></body></html>");
                return;
            }

            context.Response.Redirect(RedirectUrl.With(postLogoutRedirectUri, ("state", state)));
        }
        else
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body><h2>You have been signed out.</h2><p>You may close this window.</p></body></html>");
        }
    }
}
