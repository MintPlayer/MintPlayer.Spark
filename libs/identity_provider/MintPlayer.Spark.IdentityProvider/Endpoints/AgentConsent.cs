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
/// The consent screen of an AI agent's sign-in (<c>idp.ForMcp()</c>, <c>idp.ForMcpResource(…)</c>; MCP PRD §4.6, D5,
/// D6, D12, D22): instead of scope checkboxes, the user chooses what the agent may do.
/// <list type="bullet">
/// <item><b>This application's own endpoint</b>: which types the agent may use and whether it may make changes, limited
/// by what the application allows this agent (<c>AgentPermissions(agent)</c>) and each type's <c>agentPermissions</c>
/// (both from <see cref="IAgentExposure"/>). Allowing writes the agent grant, linked to the OAuth grant (D12).</item>
/// <item><b>Another application's endpoint</b> (remote resources): only read or read + changes, limited by that endpoint's
/// configured <see cref="OidcMcpEndpoint.Permissions"/>. This server cannot see what that application exposes, or the
/// user's rights there, so it offers no types and writes no agent grant: the agent's grant lives in that application
/// (its shadow grant, D22), where the user chooses types and turns changes on under Connected applications. Allowing
/// grants <c>mcp:write</c> only with changes chosen, the cap that application applies beside its own toggle.</item>
/// </list>
/// </summary>
/// <remarks>
/// Server-rendered like every <c>/connect</c> page, so it inherits their framing protection
/// (<c>frame-ancestors 'none'</c>, <c>X-Frame-Options: DENY</c>), antiforgery and localisation. It shows what can be
/// trusted as such: the host a metadata document was fetched from is "verified"; a self-chosen name is labelled
/// unverified; a loopback client is said to run on the user's computer; another application's endpoint is named with
/// its host.
/// <para>
/// A scoped service the two consent endpoints inject (no service location, EndpointConventionsTests R1/R2);
/// <see cref="IAgentExposure"/> is optional, present only with <c>spark.AddMcp</c>.
/// </para>
/// </remarks>
internal sealed class AgentConsent(
    AgentRequestUser users, OidcMcpResources resources, OidcAgentGrants grants, OidcSurface surface, IAgentExposure? exposure = null)
{
    /// <summary>Whether the stored authorization request is an agent's request for an MCP endpoint.</summary>
    public static bool Applies(OidcToken request) => request.Properties.ContainsKey(OidcAgentClients.RequestProperty);

    /// <summary>The user behind the consent may answer it: they exist and are not locked out ("disabled or locked accounts refused").</summary>
    public async Task<Authorization.Identity.SparkUser?> UsableUserAsync(string userId, CancellationToken ct)
    {
        var user = await users.GetAsync(userId, ct);
        return user is null || AgentRequestUser.IsLockedOut(user, DateTimeOffset.UtcNow) ? null : user;
    }

    /// <summary>The endpoint the request is for; <see langword="null"/> once it is no longer configured.</summary>
    private OidcMcpEndpoint? EndpointOf(OidcToken request) => resources.Of(request);

    /// <summary>What may be offered for this application's own endpoint: the app layer for this agent and the exposed types the user may see.</summary>
    private async Task<(EAgentPermission Max, IReadOnlyList<AgentExposedType> Types)> OfferAsync(HttpContext context, OidcApplication app)
    {
        if (exposure is null)
            return (EAgentPermission.None, []);

        var agent = new AgentInformation(OidcAgentClients.KindOf(app), app.ClientId, OidcAgentClients.VerifiedHostOf(app), app.DisplayName, context.User);
        var max = exposure.AppPermissions(agent) & (EAgentPermission.Read | EAgentPermission.Write);
        var types = (max & EAgentPermission.Read) == 0 ? [] : await exposure.ListTypesAsync(context.RequestAborted);
        return (max, types);
    }

    public async Task<IResult> RenderAsync(
        HttpContext context, ConnectText text, SparkIdentityProviderOptions options, IAntiforgery antiforgery,
        OidcApplication app, OidcToken request, string requestId)
    {
        if (EndpointOf(request) is not { } endpoint)
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorRequestExpired");

        var sb = new StringBuilder();
        await AppendIntroAsync(sb, context, text, options, app, request, endpoint);

        sb.Append("<form method=\"post\">");
        AppendAntiforgery(sb, antiforgery, context);
        AppendHidden(sb, "request_id", requestId);

        var canAllow = endpoint.IsLocal
            ? await AppendLocalChoiceAsync(sb, context, text, app)
            : AppendRemoteChoice(sb, text, endpoint);

        sb.Append("<div class=\"buttons\">");
        if (canAllow)
            sb.Append("<button type=\"submit\" name=\"decision\" value=\"allow\" class=\"btn btn-allow\">").Append(Encode(text["consentAllow"])).Append("</button>");
        sb.Append("<button type=\"submit\" name=\"decision\" value=\"deny\" class=\"btn btn-deny\">").Append(Encode(text["consentDeny"])).Append("</button>");
        sb.Append("</div></form>");
        // Only where the provider's connected-applications page is mapped; an agents-only provider leaves agents to the
        // application's account page (Connected applications).
        if (surface.ConnectedApplications)
            sb.Append("<p style=\"margin-top:24px;font-size:13px\"><a href=\"/connect/applications\">").Append(Encode(text["consentManage"])).Append("</a></p>");
        sb.Append("</body></html>");
        return ConnectResults.Html(sb.ToString());
    }

    /// <summary>The page head, the agent's trust line, the loopback notice or redirect host, the endpoint (another application's), and who is signed in.</summary>
    private async Task AppendIntroAsync(
        StringBuilder sb, HttpContext context, ConnectText text, SparkIdentityProviderOptions options, OidcApplication app, OidcToken request, OidcMcpEndpoint endpoint)
    {
        var account = await context.GetInteractiveUserNameAsync() ?? "";
        var redirectHost = Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect) ? redirect.Host : "";
        var verifiedHost = OidcAgentClients.VerifiedHostOf(app);

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
        if (!endpoint.IsLocal)
        {
            // Which application the agent will act in: the token is valid there and nowhere else (aud = this URL).
            sb.Append("<p data-agent-resource=\"").Append(Encode(endpoint.Audience)).Append("\"><strong>")
              .Append(Encode(text["consentAgentResource", endpoint.DisplayName ?? endpoint.Audience, new Uri(endpoint.Audience).Authority]))
              .Append("</strong></p>");
        }
        // "Not you?" ends the session, so it is offered only where the end-session endpoint is mapped. On an agents-only
        // provider the application is the provider (setup 3), and its own sign-out serves the wrong account.
        sb.Append("<p class=\"muted\">").Append(Encode(text["consentSignedInAs", account]));
        if (surface.EndSession)
            sb.Append(' ').Append("<a href=\"/connect/logout\">").Append(Encode(text["consentNotYou"])).Append("</a>");
        sb.Append("</p>");
    }

    /// <summary>This application's own endpoint: the types and read/changes. Answers whether anything can be allowed.</summary>
    private async Task<bool> AppendLocalChoiceAsync(StringBuilder sb, HttpContext context, ConnectText text, OidcApplication app)
    {
        var (max, types) = await OfferAsync(context, app);
        if (types.Count == 0)
        {
            sb.Append("<p class=\"notice\">").Append(Encode(text["consentAgentNothing"])).Append("</p>");
            return false;
        }

        sb.Append("<fieldset><legend>").Append(Encode(text["consentAgentTypes"])).Append("</legend>");
        sb.Append("<label><input type=\"radio\" name=\"types_mode\" value=\"all\" checked> ").Append(Encode(text["consentAgentTypesAll"])).Append("</label>");
        sb.Append("<label><input type=\"radio\" name=\"types_mode\" value=\"chosen\"> ").Append(Encode(text["consentAgentTypesChosen"])).Append("</label>");
        sb.Append("<div class=\"types\">");
        foreach (var type in types)
        {
            sb.Append("<label><input type=\"checkbox\" name=\"types\" value=\"").Append(Encode(type.Id)).Append("\"> <strong>")
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
        return true;
    }

    /// <summary>
    /// Another application's endpoint: read or read + changes only, and where the rest is decided. No types: this server
    /// cannot see what that application exposes or what the user may see there.
    /// </summary>
    private static bool AppendRemoteChoice(StringBuilder sb, ConnectText text, OidcMcpEndpoint endpoint)
    {
        var name = endpoint.DisplayName ?? endpoint.Audience;
        sb.Append("<fieldset><legend>").Append(Encode(text["consentAgentPermission"])).Append("</legend>");
        sb.Append("<label><input type=\"radio\" name=\"permission\" value=\"read\" checked> ").Append(Encode(text["consentAgentRead"])).Append("</label>");
        if ((endpoint.Permissions & EAgentPermission.Write) != 0)
            sb.Append("<label><input type=\"radio\" name=\"permission\" value=\"write\"> ").Append(Encode(text["consentAgentRemoteWrite", name])).Append("</label>");
        sb.Append("</fieldset>");
        sb.Append("<p class=\"muted\" data-agent-remote-grant>").Append(Encode(text["consentAgentRemoteManage", name])).Append("</p>");
        sb.Append("<p class=\"muted\">").Append(Encode(text["consentAgentRights"])).Append("</p>");
        return true;
    }

    /// <summary>The choice as posted.</summary>
    public sealed record Choice(string? TypesMode, string[] Types, string? Permission);

    /// <summary>
    /// Records an "allow": validates the choice against what may be offered (a forged POST cannot widen it), writes
    /// the OAuth grant (and, for this application's own endpoint, the agent grant), and answers the code.
    /// </summary>
    public async Task<IResult> AllowAsync(
        HttpContext context, IAsyncDocumentSession session, ConnectText text, SparkIdentityProviderOptions options, OidcAudit audit,
        string issuer, OidcApplication app, OidcToken request, Authorization.Identity.SparkUser user, Choice choice, CancellationToken ct)
    {
        if (EndpointOf(request) is not { } endpoint)
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorRequestExpired");

        var permissions = choice.Permission == "write" ? EAgentPermission.Read | EAgentPermission.Write : EAgentPermission.Read;
        List<AgentGrantType>? types = null;
        if (endpoint.IsLocal)
        {
            var (max, offered) = await OfferAsync(context, app);
            if (offered.Count == 0 || (permissions & max) != permissions)
                return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorAgentChoice");

            if (choice.TypesMode == "chosen")
            {
                // The form posts type ids (D28): a grant stores the id, which a rename never changes.
                var exposed = offered.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
                types = [];
                foreach (var typeId in choice.Types.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!exposed.TryGetValue(typeId, out var type))
                        return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorAgentChoice");
                    types.Add(new AgentGrantType { TypeId = type.Id, Permissions = (type.Permissions & permissions) | EAgentPermission.Read });
                }
                if (types.Count == 0)
                    return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorAgentChoice");
            }
            else if (choice.TypesMode != "all")
            {
                return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorAgentChoice");
            }
        }
        else if ((permissions & endpoint.Permissions) != permissions || choice.TypesMode is not (null or "all") || choice.Types.Length > 0)
        {
            // Another application's endpoint offers no types (its grant lives there) and changes only where configured: a
            // forged choice is refused, not trimmed.
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorAgentChoice");
        }

        // The token's scopes say what was granted; mcp:write only with changes allowed, so a token without it can be
        // answered with insufficient_scope (D6), and another application's endpoint caps changes by it (D22). For this
        // application's own endpoint the grant, read on every call, still decides.
        var scopes = request.Scopes
            .Where(s => s != OidcAgentClients.WriteScope || permissions.HasFlag(EAgentPermission.Write))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        request.Scopes = scopes;
        request.AuthorizationId = await OidcAuthorizationFlow.EnsureAuthorizationAsync(session, app, user.Id!, scopes, remember: false, ct);
        if (endpoint.IsLocal)
        {
            await grants
                .GrantAsync(user.Id!, user.SecurityStamp, app, request.AuthorizationId, permissions, types, ct);
        }
        await audit.RecordAsync(session, OidcAuditKinds.ConsentGranted, user.Id, app.Id, user.Id, context.Connection.RemoteIpAddress?.ToString(),
            new Dictionary<string, string>
            {
                ["scopes"] = string.Join(' ', scopes),
                ["agent"] = OidcAgentClients.KindOf(app).ToString(),
                ["resource"] = endpoint.Audience,
                ["permissions"] = permissions.ToString(),
                ["types"] = types is null ? "*" : string.Join(",", types.Select(t => t.TypeId)),
            }, ct);

        return await OidcAuthorizationFlow.IssueCodeResponseAsync(session, request, issuer, ct);
    }
}
