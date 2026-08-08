using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Handles both GET (render consent form) and POST (process consent decision).
/// Renders a minimal inline HTML form (no Razor infrastructure needed).
/// <para>
/// Both handlers take a single input from the browser: the <c>request_id</c> minted by
/// <c>/connect/authorize</c>. Everything the flow acts on — client, redirect URI, scopes,
/// PKCE challenge, nonce, state — is read from the stored
/// <see cref="OidcAuthorizationRequest"/>, never from the query string or the form. That is
/// what makes this endpoint safe by construction rather than by remembering to repeat
/// <c>/connect/authorize</c>'s checks.
/// </para>
/// </summary>
internal static class Consent
{
    public static async Task HandleGet(HttpContext context)
    {
        var ct = context.RequestAborted;

        var requestId = context.Request.Query["request_id"].FirstOrDefault();
        if (string.IsNullOrEmpty(requestId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing parameters.");
            return;
        }

        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var request = await Authorize.LoadPendingRequestAsync(session, requestId, userId, ct);
        if (request == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("This authorization request is no longer valid. Please start again.");
            return;
        }

        var app = await session.LoadAsync<OidcApplication>(request.ApplicationId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Unknown client.");
            return;
        }

        // Load scope definitions
        var requestedScopes = request.Scopes;
        var scopeDefinitions = await session
            .Query<OidcScope>()
            .Where(s => s.Name.In(requestedScopes))
            .ToListAsync(ct);

        // Render minimal consent page
        context.Response.ContentType = "text/html; charset=utf-8";

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head>");
        sb.Append("<title>Authorize ").Append(Encode(app.DisplayName)).Append("</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;max-width:480px;margin:60px auto;padding:0 20px}");
        sb.Append("h2{color:#333}.scope-list{list-style:none;padding:0}");
        sb.Append(".scope-list li{padding:8px 0;border-bottom:1px solid #eee}");
        sb.Append(".scope-list input[type=checkbox]{margin-right:8px}");
        sb.Append(".buttons{margin-top:24px;display:flex;gap:12px}");
        sb.Append(".btn{padding:10px 24px;border:none;border-radius:6px;font-size:14px;cursor:pointer}");
        sb.Append(".btn-allow{background:#0d6efd;color:white}.btn-deny{background:#6c757d;color:white}");
        sb.Append("</style></head><body>");
        sb.Append("<h2>").Append(Encode(app.DisplayName)).Append(" wants to access your account</h2>");
        sb.Append("<p>This application is requesting the following permissions:</p>");
        sb.Append("<form method=\"post\"><ul class=\"scope-list\">");

        foreach (var s in requestedScopes)
        {
            var def = scopeDefinitions.FirstOrDefault(d => d.Name == s);
            var displayName = def?.DisplayName ?? s;
            var description = def?.Description ?? "";
            var isRequired = def?.Required ?? (s == "openid");
            var isEmphasized = def?.Emphasize ?? false;

            sb.Append("<li");
            if (isEmphasized) sb.Append(" style=\"background:#fff3cd;padding:8px;border-radius:4px\"");
            sb.Append("><label>");
            sb.Append("<input type=\"checkbox\" name=\"scopes\" value=\"").Append(Encode(s)).Append("\" checked");
            if (isRequired) sb.Append(" disabled");
            sb.Append(" />");
            if (isEmphasized) sb.Append("<strong style=\"color:#856404\">⚠ ");
            else sb.Append("<strong>");
            sb.Append(Encode(displayName));
            if (isEmphasized) sb.Append("</strong>");
            else sb.Append("</strong>");
            if (!string.IsNullOrEmpty(description))
                sb.Append(" &mdash; ").Append(Encode(description));
            sb.Append("</label>");
            if (isRequired)
                sb.Append("<input type=\"hidden\" name=\"scopes\" value=\"").Append(Encode(s)).Append("\" />");
            sb.Append("</li>");
        }

        sb.Append("</ul>");

        // The only thing the form carries back is the handle.
        AppendHidden(sb, "request_id", requestId);

        sb.Append("<div class=\"buttons\">");
        sb.Append("<button type=\"submit\" name=\"decision\" value=\"allow\" class=\"btn btn-allow\">Allow</button>");
        sb.Append("<button type=\"submit\" name=\"decision\" value=\"deny\" class=\"btn btn-deny\">Deny</button>");
        sb.Append("</div></form></body></html>");

        await context.Response.WriteAsync(sb.ToString());
    }

    public static async Task HandlePost(HttpContext context)
    {
        var ct = context.RequestAborted;
        var form = await context.Request.ReadFormAsync(ct);

        var requestId = form["request_id"].FirstOrDefault();
        var decision = form["decision"].FirstOrDefault();

        if (string.IsNullOrEmpty(requestId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing parameters.");
            return;
        }

        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Not authenticated.");
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var request = await Authorize.LoadPendingRequestAsync(session, requestId, userId, ct);
        if (request == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("This authorization request is no longer valid. Please start again.");
            return;
        }

        // The redirect target comes from the stored request, which /connect/authorize already
        // matched against the application's registered URIs — so even the denial path cannot
        // be pointed somewhere of the caller's choosing.
        if (decision != "allow")
        {
            request.Status = "denied";
            await session.SaveChangesAsync(ct);

            var denyUrl = $"{request.RedirectUri}?error=access_denied&error_description=The+user+denied+the+request.";
            if (!string.IsNullOrEmpty(request.State))
                denyUrl += $"&state={Uri.EscapeDataString(request.State)}";
            context.Response.Redirect(denyUrl);
            return;
        }

        var app = await session.LoadAsync<OidcApplication>(request.ApplicationId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Unknown client.");
            return;
        }

        // The checkboxes can only narrow what the request already carries. They are attacker-
        // controlled markup, so a crafted POST must not be able to grant a scope the client was
        // never allowed — and request.Scopes was intersected with AllowedScopes upstream.
        var grantedScopes = form["scopes"]
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Where(s => request.Scopes.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (grantedScopes.Count == 0)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("No permitted scopes were granted.");
            return;
        }

        request.Scopes = grantedScopes;
        request.AuthorizationId = await Authorize.EnsureAuthorizationAsync(session, app, userId, grantedScopes, ct);

        await Authorize.GenerateCodeAndRedirectAsync(context, session, request, ct);
    }

    private static string Encode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private static void AppendHidden(StringBuilder sb, string name, string? value)
    {
        sb.Append("<input type=\"hidden\" name=\"").Append(Encode(name))
          .Append("\" value=\"").Append(Encode(value ?? "")).Append("\" />");
    }
}
