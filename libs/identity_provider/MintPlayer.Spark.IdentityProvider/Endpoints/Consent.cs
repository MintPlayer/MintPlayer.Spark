using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using static MintPlayer.Spark.IdentityProvider.Endpoints.ConnectPage;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

// The consent screen: GET renders it, POST records the decision. Rendered as minimal inline HTML
// (no Razor infrastructure needed).
//
// Both take a single input from the browser: the request_id minted by /connect/authorize.
// Everything the flow acts on — client, redirect URI, scopes, PKCE challenge, nonce, state — is
// read from the stored authorization request (an OidcToken), never from the query string or the form. That is
// what makes this endpoint safe by construction rather than by remembering to repeat
// /connect/authorize's checks.

/// <summary>Renders the consent screen (<c>GET /connect/consent</c>).</summary>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcConsentPage : IGetEndpoint<string>
{
    public static string Path => "/consent";

    // Optional: a missing handle is this page's own 400, not the binder's.
    [QueryParam("request_id")] public string? RequestId { get; set; }

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IAntiforgery antiforgery;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ConnectText text;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var context = httpContextAccessor.HttpContext!;
        var requestId = RequestId;

        if (string.IsNullOrEmpty(requestId))
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorMissingParameters");

        var userId = await context.GetInteractiveUserIdAsync();
        if (string.IsNullOrEmpty(userId))
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            return Results.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        using var session = store.OpenAsyncSession();

        var request = await OidcAuthorizationFlow.LoadPendingRequestAsync(session, requestId, userId, ct);
        if (request == null)
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorRequestExpired");
        text.UseUiLocales(request.Properties.GetValueOrDefault("ui_locales"));

        var app = await session.LoadAsync<OidcApplication>(request.ApplicationId, ct);
        if (app == null || !app.Enabled)
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorUnknownClient");

        // Load scope definitions
        var requestedScopes = request.Scopes;
        var scopeDefinitions = await OidcScopeCatalog.LoadAsync(session, requestedScopes, ct);

        // D6: what the user already allowed is shown as allowed, and only the rest is asked.
        var grant = await session.LoadAsync<OidcGrant>(OidcGrantReference.DocumentId(userId, app.Id!), ct);
        var alreadyGranted = grant is { Status: "valid" }
            ? grant.GrantedScopes.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var account = await context.GetInteractiveUserNameAsync() ?? "";
        var redirectHost = Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect) ? redirect.Host : "";

        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, context, text["consentTitle", app.DisplayName], text.Culture, options.Branding);
        sb.Append("body{max-width:480px;margin:60px auto;padding:0 20px}");
        sb.Append(".app{display:flex;align-items:center;gap:12px}.app img{width:48px;height:48px;border-radius:8px;object-fit:contain}");
        sb.Append(".muted{color:var(--idp-muted);font-size:13px}");
        sb.Append(".scope-list{list-style:none;padding:0}");
        sb.Append(".scope-list li{padding:8px 0;border-bottom:1px solid var(--idp-border)}");
        sb.Append(".scope-list input[type=checkbox]{margin-right:8px}");
        sb.Append(".scope-list li.emphasized{background:var(--idp-warning-bg);padding:8px;border-radius:4px}.scope-list li.emphasized strong{color:var(--idp-warning-color)}");
        sb.Append(".buttons{margin-top:24px;display:flex;gap:12px}");
        sb.Append(".btn{padding:10px 24px;border:none;border-radius:6px;font-size:14px;cursor:pointer}");
        sb.Append(".btn-allow{background:var(--idp-primary);color:#fff}.btn-deny{background:var(--idp-secondary);color:#fff}");
        sb.Append("a{color:var(--idp-link)}");
        sb.Append("</style></head><body>");
        ConnectPageTheme.AppendBrand(sb, options.Branding);

        // Who is asking: logo, name, publisher, and where the browser goes next (D6).
        sb.Append("<div class=\"app\">");
        if (!string.IsNullOrEmpty(app.LogoUrl))
            sb.Append("<img src=\"").Append(Encode(app.LogoUrl)).Append("\" alt=\"\">");
        sb.Append("<div><h2 style=\"margin:0\">").Append(Encode(text["consentHeading", app.DisplayName])).Append("</h2>");
        if (!string.IsNullOrEmpty(app.Publisher))
            sb.Append("<div class=\"muted\">").Append(Encode(text["consentPublisher", app.Publisher])).Append("</div>");
        sb.Append("</div></div>");
        if (redirectHost.Length > 0)
            sb.Append("<p class=\"muted\">").Append(Encode(text["consentRedirect", redirectHost])).Append("</p>");

        // As whom: the signed-in account, with a way out when it is the wrong one.
        sb.Append("<p class=\"muted\">").Append(Encode(text["consentSignedInAs", account])).Append(' ')
          .Append("<a href=\"/connect/logout\">").Append(Encode(text["consentNotYou"])).Append("</a></p>");

        sb.Append("<p>").Append(Encode(text["consentIntro"])).Append("</p>");
        sb.Append("<form method=\"post\">");
        AppendAntiforgery(sb, antiforgery, context);
        sb.Append("<ul class=\"scope-list\">");

        foreach (var s in requestedScopes)
        {
            var def = scopeDefinitions.FirstOrDefault(d => string.Equals(d.Name, s, StringComparison.OrdinalIgnoreCase));
            var displayName = text.Of(def?.DisplayName, s);
            var description = text.Of(def?.Description, "");
            // Required by the scope itself (openid), or by this application (D6: per-app required/optional).
            var isRequired = s == "openid"
                || def?.Required == true
                || app.Scopes.Any(a => a.Required && string.Equals(a.Name, s, StringComparison.OrdinalIgnoreCase));
            var granted = alreadyGranted.Contains(s);
            var isEmphasized = def?.Emphasize ?? false;

            sb.Append("<li");
            if (isEmphasized && !granted) sb.Append(" class=\"emphasized\"");
            sb.Append("><label>");
            sb.Append("<input type=\"checkbox\" name=\"scopes\" value=\"").Append(Encode(s)).Append("\" checked");
            if (isRequired || granted) sb.Append(" disabled");
            sb.Append(" />");
            sb.Append(isEmphasized && !granted ? "<strong>⚠ " : "<strong>");
            sb.Append(Encode(displayName)).Append("</strong>");
            if (!string.IsNullOrEmpty(description))
                sb.Append(" &mdash; ").Append(Encode(description));
            if (granted)
                sb.Append(" <span class=\"muted\">(").Append(Encode(text["consentAlreadyAllowed"])).Append(")</span>");
            sb.Append("</label>");
            // A disabled checkbox is not submitted; what it shows as ticked is carried by a hidden field,
            // and the POST re-adds required scopes on the server anyway.
            if (isRequired || granted)
                sb.Append("<input type=\"hidden\" name=\"scopes\" value=\"").Append(Encode(s)).Append("\" />");
            sb.Append("</li>");
        }

        sb.Append("</ul>");

        if (app.AllowRememberConsent)
        {
            sb.Append("<p><label><input type=\"checkbox\" name=\"remember\" value=\"true\" checked /> ")
              .Append(Encode(text["consentRemember"])).Append("</label></p>");
        }

        // The only thing the form carries back is the handle.
        AppendHidden(sb, "request_id", requestId);

        sb.Append("<div class=\"buttons\">");
        sb.Append("<button type=\"submit\" name=\"decision\" value=\"allow\" class=\"btn btn-allow\">").Append(Encode(text["consentAllow"])).Append("</button>");
        sb.Append("<button type=\"submit\" name=\"decision\" value=\"deny\" class=\"btn btn-deny\">").Append(Encode(text["consentDeny"])).Append("</button>");
        sb.Append("</div></form>");

        // D6: the application's own policies, where it has them.
        if (!string.IsNullOrEmpty(app.PrivacyPolicyUrl) || !string.IsNullOrEmpty(app.TermsOfServiceUrl))
        {
            sb.Append("<p class=\"muted\" style=\"margin-top:16px\">");
            if (!string.IsNullOrEmpty(app.PrivacyPolicyUrl))
                sb.Append("<a href=\"").Append(Encode(app.PrivacyPolicyUrl)).Append("\" target=\"_blank\" rel=\"noopener\">").Append(Encode(text["consentPrivacy"])).Append("</a> ");
            if (!string.IsNullOrEmpty(app.TermsOfServiceUrl))
                sb.Append("<a href=\"").Append(Encode(app.TermsOfServiceUrl)).Append("\" target=\"_blank\" rel=\"noopener\">").Append(Encode(text["consentTerms"])).Append("</a>");
            sb.Append("</p>");
        }

        // The consent screen is the only place a user is reliably told this decision exists, so it
        // is the only place they will think to look for undoing it. A withdrawal page nothing
        // links to is a withdrawal page nobody finds.
        sb.Append("<p style=\"margin-top:24px;font-size:13px\">")
          .Append("<a href=\"/connect/applications\">").Append(Encode(text["consentManage"])).Append("</a>")
          .Append("</p>");

        sb.Append("</body></html>");

        return ConnectResults.Html(sb.ToString());
    }
}

/// <summary>The consent form as posted: the handle, the button pressed, and the ticked scopes.</summary>
internal sealed record OidcConsentSubmission(string? RequestId, string? Decision, string[] Scopes, bool Remember = false);

/// <summary>Records the consent decision (<c>POST /connect/consent</c>).</summary>
/// <remarks>
/// The body is the page's own HTML form, so <see cref="BindRequestAsync"/> reads it as one. A body
/// that is not a form fails exactly as it did when the handler read the form itself.
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcConsentSubmit : IPostEndpoint<OidcConsentSubmission>
{
    public static string Path => "/consent";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly ConnectText text;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    /// <summary>Kept from <see cref="BindRequestAsync"/> (D8); the endpoint is created per request.</summary>
    private HttpContext context = null!;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    protected override async ValueTask<OidcConsentSubmission?> BindRequestAsync(HttpContext context)
    {
        this.context = context;
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcConsentSubmission(
            RequestId: form["request_id"].FirstOrDefault(),
            Decision: form["decision"].FirstOrDefault(),
            Scopes: [.. form["scopes"].Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)],
            Remember: string.Equals(form["remember"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase));
    }

    public override async Task<IResult> HandleAsync(OidcConsentSubmission submission, CancellationToken ct)
    {
        var requestId = submission.RequestId;

        if (string.IsNullOrEmpty(requestId))
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorMissingParameters");

        var userId = await context.GetInteractiveUserIdAsync();
        if (string.IsNullOrEmpty(userId))
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status401Unauthorized, "errorNotAuthenticated");

        using var session = store.OpenAsyncSession();

        var request = await OidcAuthorizationFlow.LoadPendingRequestAsync(session, requestId, userId, ct);
        if (request == null)
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorRequestExpired");
        text.UseUiLocales(request.Properties.GetValueOrDefault("ui_locales"));

        // The redirect target comes from the stored request, which /connect/authorize already
        // matched against the application's registered URIs — so even the denial path cannot
        // be pointed somewhere of the caller's choosing.
        if (submission.Decision != "allow")
        {
            request.Status = "denied";
            await session.SaveChangesAsync(ct);

            // Delivered the way the client asked (response_mode), with iss (RFC 9207).
            return OidcAuthorizationResponse.Error(request.RedirectUri!, request.Properties.GetValueOrDefault("response_mode"),
                oidcIssuer.Resolve(context.Request), request.State, "access_denied", "The user denied the request.");
        }

        var app = await session.LoadAsync<OidcApplication>(request.ApplicationId, ct);
        if (app == null || !app.Enabled)
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorUnknownClient");

        // The checkboxes can only narrow what the request already carries. They are attacker-
        // controlled markup, so a crafted POST must not be able to grant a scope the client was
        // never allowed — and request.Scopes was intersected with AllowedScopes upstream.
        var grantedScopes = submission.Scopes
            .Where(s => request.Scopes.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A scope marked Required is not the user's to decline: the page renders it as a
        // disabled checkbox, but that is markup, and a forged POST simply omits it. Re-adding
        // it here is what makes Required mean something on the server rather than in the UI.
        var requiredScopes = (await OidcScopeCatalog.LoadAsync(session, request.Scopes, ct))
            .Where(s => s.Required || app.Scopes.Any(a => a.Required && string.Equals(a.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var required in requiredScopes)
        {
            if (!grantedScopes.Contains(required.Name, StringComparer.OrdinalIgnoreCase))
                grantedScopes.Add(required.Name);
        }

        if (grantedScopes.Count == 0)
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest, "errorNoScopes");

        request.Scopes = grantedScopes;
        request.AuthorizationId = await OidcAuthorizationFlow.EnsureAuthorizationAsync(session, app, userId, grantedScopes, submission.Remember, ct);
        await audit.RecordAsync(session, OidcAuditKinds.ConsentGranted, userId, app.Id, userId, context.Connection.RemoteIpAddress?.ToString(),
            new Dictionary<string, string> { ["scopes"] = string.Join(' ', grantedScopes), ["remembered"] = submission.Remember.ToString() }, ct);

        return await OidcAuthorizationFlow.IssueCodeResponseAsync(session, request, oidcIssuer.Resolve(context.Request), ct);
    }
}
