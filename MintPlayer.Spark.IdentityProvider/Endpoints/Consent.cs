using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Handles both GET (render consent form) and POST (process consent decision).
/// Renders a minimal inline HTML form (no Razor infrastructure needed).
/// </summary>
internal static class Consent
{
    public static async Task HandleGet(HttpContext context)
    {
        var ct = context.RequestAborted;
        var query = context.Request.Query;

        var clientId = query["client_id"].FirstOrDefault();
        var scope = query["scope"].FirstOrDefault();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(scope))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing parameters.");
            return;
        }

        // Check authentication
        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        // Load application
        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();
        var app = await Authorize.FindApplicationByClientIdAsync(session, clientId, ct);

        if (app == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Unknown client.");
            return;
        }

        var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Load scope definitions
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

        // Pass through all original query params as hidden fields
        AppendHidden(sb, "client_id", query["client_id"].FirstOrDefault());
        AppendHidden(sb, "redirect_uri", query["redirect_uri"].FirstOrDefault());
        AppendHidden(sb, "scope", query["scope"].FirstOrDefault());
        AppendHidden(sb, "state", query["state"].FirstOrDefault());
        AppendHidden(sb, "code_challenge", query["code_challenge"].FirstOrDefault());
        AppendHidden(sb, "code_challenge_method", query["code_challenge_method"].FirstOrDefault());
        AppendHidden(sb, "nonce", query["nonce"].FirstOrDefault());
        AppendHidden(sb, "response_type", "code");

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

        var decision = form["decision"].FirstOrDefault();
        var clientId = form["client_id"].FirstOrDefault();
        var redirectUri = form["redirect_uri"].FirstOrDefault();
        var scope = form["scope"].FirstOrDefault();
        var state = form["state"].FirstOrDefault();
        var codeChallenge = form["code_challenge"].FirstOrDefault();
        var codeChallengeMethod = form["code_challenge_method"].FirstOrDefault();
        var nonce = form["nonce"].FirstOrDefault();

        if (string.IsNullOrEmpty(redirectUri))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing redirect URI.");
            return;
        }

        if (decision != "allow")
        {
            var denyUrl = $"{redirectUri}?error=access_denied&error_description=The+user+denied+the+request.";
            if (!string.IsNullOrEmpty(state))
                denyUrl += $"&state={Uri.EscapeDataString(state)}";
            context.Response.Redirect(denyUrl);
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

        var app = await Authorize.FindApplicationByClientIdAsync(session, clientId!, ct);
        if (app == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Unknown client.");
            return;
        }

        // Collect granted scopes (from checkboxes)
        var grantedScopes = form["scopes"].Where(s => s != null).Select(s => s!).ToList();
        if (grantedScopes.Count == 0 && !string.IsNullOrEmpty(scope))
        {
            grantedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // Create or update authorization
        var existingAuth = await session
            .Query<OidcAuthorization, OidcAuthorizations_BySubjectAndApplication>()
            .Where(a => a.Subject == userId && a.ApplicationId == app.Id! && a.Status == "valid")
            .FirstOrDefaultAsync(ct);

        if (existingAuth != null)
        {
            foreach (var s in grantedScopes)
            {
                if (!existingAuth.GrantedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
                    existingAuth.GrantedScopes.Add(s);
            }
        }
        else
        {
            var auth = new OidcAuthorization
            {
                ApplicationId = app.Id!,
                Subject = userId,
                Status = "valid",
                GrantedScopes = grantedScopes,
                CreatedAt = DateTime.UtcNow,
            };
            await session.StoreAsync(auth, ct);
        }

        await session.SaveChangesAsync(ct);

        await Authorize.GenerateCodeAndRedirectAsync(
            context, session, app, userId, grantedScopes,
            redirectUri, state, codeChallenge, codeChallengeMethod, nonce, ct);
    }

    private static string Encode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private static void AppendHidden(StringBuilder sb, string name, string? value)
    {
        sb.Append("<input type=\"hidden\" name=\"").Append(Encode(name))
          .Append("\" value=\"").Append(Encode(value ?? "")).Append("\" />");
    }
}
