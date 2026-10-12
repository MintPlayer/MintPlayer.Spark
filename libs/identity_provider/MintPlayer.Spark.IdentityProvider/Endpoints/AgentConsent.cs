using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Mcp;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents.Session;
using static MintPlayer.Spark.IdentityProvider.Endpoints.ConnectPage;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// The consent screen of an AI agent's sign-in (<c>idp.ForMcp()</c>; MCP PRD §4.6, D5, D6, D12): instead of scope
/// checkboxes, the user chooses which types the agent may use and whether it may make changes, limited by what the
/// application allows this agent (<c>AgentPermissions(agent)</c>) and each type's <c>agentPermissions</c> (both from
/// <see cref="IAgentExposure"/>). Allowing writes the agent grant, linked to the OAuth grant (D12).
/// </summary>
/// <remarks>
/// Server-rendered like every <c>/connect</c> page, so it inherits their framing protection
/// (<c>frame-ancestors 'none'</c>, <c>X-Frame-Options: DENY</c>), antiforgery and localisation. It shows what can be
/// trusted as such: the host a metadata document was fetched from is "verified"; a self-chosen name is labelled
/// unverified; a loopback client is said to run on the user's computer.
/// </remarks>
internal static class AgentConsent
{
    /// <summary>Whether the stored authorization request is an agent's request for the MCP resource.</summary>
    public static bool Applies(OidcToken request) => request.Properties.ContainsKey(OidcAgentClients.RequestProperty);

    /// <summary>The user behind the consent may answer it: they exist and are not locked out ("disabled or locked accounts refused").</summary>
    public static async Task<Authorization.Identity.SparkUser?> UsableUserAsync(HttpContext context, string userId)
    {
        var user = await context.RequestServices.GetRequiredService<AgentRequestUser>().GetAsync(userId, context.RequestAborted);
        return user is null || AgentRequestUser.IsLockedOut(user, DateTimeOffset.UtcNow) ? null : user;
    }

    /// <summary>What may be offered: the app layer for this agent and the exposed types the user may see.</summary>
    private static async Task<(EAgentPermission Max, IReadOnlyList<AgentExposedType> Types)> OfferAsync(HttpContext context, OidcApplication app)
    {
        if (context.RequestServices.GetService<IAgentExposure>() is not { } exposure)
            return (EAgentPermission.None, []);

        var agent = new AgentInformation(OidcAgentClients.KindOf(app), app.ClientId, OidcAgentClients.VerifiedHostOf(app), app.DisplayName, context.User);
        var max = exposure.AppPermissions(agent) & (EAgentPermission.Read | EAgentPermission.Write);
        var types = (max & EAgentPermission.Read) == 0 ? [] : await exposure.ListTypesAsync(context.RequestAborted);
        return (max, types);
    }

    public static async Task<IResult> RenderAsync(
        HttpContext context, ConnectText text, SparkIdentityProviderOptions options, IAntiforgery antiforgery,
        OidcApplication app, OidcToken request, string requestId)
    {
        var (max, types) = await OfferAsync(context, app);
        var account = await context.GetInteractiveUserNameAsync() ?? "";
        var redirectHost = Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect) ? redirect.Host : "";
        var verifiedHost = OidcAgentClients.VerifiedHostOf(app);

        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, context, text["consentTitle", app.DisplayName], text.Culture, options.Branding);
        sb.Append("body{max-width:520px;margin:60px auto;padding:0 20px}");
        sb.Append(".muted{color:var(--idp-muted);font-size:13px}");
        sb.Append(".notice{background:var(--idp-warning-bg);color:var(--idp-warning-color);padding:10px;border-radius:4px}");
        sb.Append("fieldset{border:1px solid var(--idp-border);border-radius:6px;margin:16px 0;padding:8px 12px}");
        sb.Append("fieldset label{display:block;padding:4px 0}.types{margin-left:24px}");
        sb.Append(".buttons{margin-top:24px;display:flex;gap:12px}");
        sb.Append(".btn{padding:10px 24px;border:none;border-radius:6px;font-size:14px;cursor:pointer}");
        sb.Append(".btn-allow{background:var(--idp-primary);color:#fff}.btn-deny{background:var(--idp-secondary);color:#fff}");
        sb.Append("a{color:var(--idp-link)}");
        sb.Append("</style></head><body>");
        ConnectPageTheme.AppendBrand(sb, options.Branding);

        sb.Append("<h2>").Append(Encode(text["consentAgentHeading", app.DisplayName])).Append("</h2>");
        sb.Append("<p class=\"muted\" data-agent-trust=\"").Append(verifiedHost is null ? "unverified" : "verified").Append("\">")
          .Append(Encode(verifiedHost is null ? text["consentAgentUnverified"] : text["consentAgentVerified", verifiedHost])).Append("</p>");
        if (OidcAgentClients.IsLoopback(request.RedirectUri))
            sb.Append("<p class=\"notice\" data-agent-loopback>").Append(Encode(text["consentAgentLoopback"])).Append("</p>");
        else if (redirectHost.Length > 0)
            sb.Append("<p class=\"muted\">").Append(Encode(text["consentRedirect", redirectHost])).Append("</p>");
        sb.Append("<p class=\"muted\">").Append(Encode(text["consentSignedInAs", account])).Append(' ')
          .Append("<a href=\"/connect/logout\">").Append(Encode(text["consentNotYou"])).Append("</a></p>");

        sb.Append("<form method=\"post\">");
        AppendAntiforgery(sb, antiforgery, context);
        AppendHidden(sb, "request_id", requestId);

        if (types.Count == 0)
        {
            sb.Append("<p class=\"notice\">").Append(Encode(text["consentAgentNothing"])).Append("</p>");
        }
        else
        {
            sb.Append("<fieldset><legend>").Append(Encode(text["consentAgentTypes"])).Append("</legend>");
            sb.Append("<label><input type=\"radio\" name=\"types_mode\" value=\"all\" checked> ").Append(Encode(text["consentAgentTypesAll"])).Append("</label>");
            sb.Append("<label><input type=\"radio\" name=\"types_mode\" value=\"chosen\"> ").Append(Encode(text["consentAgentTypesChosen"])).Append("</label>");
            sb.Append("<div class=\"types\">");
            foreach (var type in types)
            {
                sb.Append("<label><input type=\"checkbox\" name=\"types\" value=\"").Append(Encode(type.Name)).Append("\"> <strong>")
                  .Append(Encode(type.Label)).Append("</strong>");
                if (type.Queries.Count > 0)
                    sb.Append(" <span class=\"muted\">").Append(Encode(text["consentAgentQueries", string.Join(", ", type.Queries)])).Append("</span>");
                sb.Append("</label>");
            }
            sb.Append("</div></fieldset>");

            sb.Append("<fieldset><legend>").Append(Encode(text["consentAgentPermission"])).Append("</legend>");
            sb.Append("<label><input type=\"radio\" name=\"permission\" value=\"read\" checked> ").Append(Encode(text["consentAgentRead"])).Append("</label>");
            if ((max & EAgentPermission.Write) != 0 && types.Any(t => (t.Permissions & EAgentPermission.Write) != 0))
                sb.Append("<label><input type=\"radio\" name=\"permission\" value=\"write\"> ").Append(Encode(text["consentAgentWrite"])).Append("</label>");
            sb.Append("</fieldset>");
            sb.Append("<p class=\"muted\">").Append(Encode(text["consentAgentRights"])).Append("</p>");
        }

        sb.Append("<div class=\"buttons\">");
        if (types.Count > 0)
            sb.Append("<button type=\"submit\" name=\"decision\" value=\"allow\" class=\"btn btn-allow\">").Append(Encode(text["consentAllow"])).Append("</button>");
        sb.Append("<button type=\"submit\" name=\"decision\" value=\"deny\" class=\"btn btn-deny\">").Append(Encode(text["consentDeny"])).Append("</button>");
        sb.Append("</div></form>");
        sb.Append("<p style=\"margin-top:24px;font-size:13px\"><a href=\"/connect/applications\">").Append(Encode(text["consentManage"])).Append("</a></p>");
        sb.Append("</body></html>");
        return ConnectResults.Html(sb.ToString());
    }

    /// <summary>The choice as posted.</summary>
    public sealed record Choice(string? TypesMode, string[] Types, string? Permission);

    /// <summary>
    /// Records an "allow": validates the choice against what may be offered (a forged POST cannot widen it), writes
    /// the OAuth grant and the agent grant, and answers the code. <see langword="null"/> grant = refused choice.
    /// </summary>
    public static async Task<IResult> AllowAsync(
        HttpContext context, IAsyncDocumentSession session, ConnectText text, SparkIdentityProviderOptions options, OidcAudit audit,
        string issuer, OidcApplication app, OidcToken request, Authorization.Identity.SparkUser user, Choice choice, CancellationToken ct)
    {
        var (max, offered) = await OfferAsync(context, app);
        var permissions = choice.Permission == "write" ? EAgentPermission.Read | EAgentPermission.Write : EAgentPermission.Read;
        if (offered.Count == 0 || (permissions & max) != permissions)
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorAgentChoice");

        List<AgentGrantType>? types = null;
        if (choice.TypesMode == "chosen")
        {
            var exposed = offered.ToDictionary(t => t.Name, StringComparer.Ordinal);
            types = [];
            foreach (var name in choice.Types.Distinct(StringComparer.Ordinal))
            {
                if (!exposed.TryGetValue(name, out var type))
                    return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorAgentChoice");
                types.Add(new AgentGrantType { Name = name, Permissions = (type.Permissions & permissions) | EAgentPermission.Read });
            }
            if (types.Count == 0)
                return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorAgentChoice");
        }
        else if (choice.TypesMode != "all")
        {
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorAgentChoice");
        }

        // The token's scopes say what was granted; mcp:write only with changes allowed, so a token without it can be
        // answered with insufficient_scope (D6). The grant, read on every call, still decides.
        var scopes = request.Scopes
            .Where(s => s != OidcAgentClients.WriteScope || permissions.HasFlag(EAgentPermission.Write))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        request.Scopes = scopes;
        request.AuthorizationId = await OidcAuthorizationFlow.EnsureAuthorizationAsync(session, app, user.Id!, scopes, remember: false, ct);
        await context.RequestServices.GetRequiredService<OidcAgentGrants>()
            .GrantAsync(user.Id!, user.SecurityStamp, app, request.AuthorizationId, permissions, types, ct);
        await audit.RecordAsync(session, OidcAuditKinds.ConsentGranted, user.Id, app.Id, user.Id, context.Connection.RemoteIpAddress?.ToString(),
            new Dictionary<string, string>
            {
                ["scopes"] = string.Join(' ', scopes),
                ["agent"] = OidcAgentClients.KindOf(app).ToString(),
                ["permissions"] = permissions.ToString(),
                ["types"] = types is null ? "*" : string.Join(',', types.Select(t => t.Name)),
            }, ct);

        return await OidcAuthorizationFlow.IssueCodeResponseAsync(session, request, issuer, ct);
    }
}
