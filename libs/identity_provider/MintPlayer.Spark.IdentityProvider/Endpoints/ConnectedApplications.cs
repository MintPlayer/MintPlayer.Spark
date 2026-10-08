using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using static MintPlayer.Spark.IdentityProvider.Endpoints.ConnectPage;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

// Where a user sees what they have granted, and takes it back.
//
// Consent was recorded from the first commit and consulted nowhere, and there was no way to
// withdraw it: RFC 7009's /connect/revoke is client-facing — it demands client credentials and
// refuses a token not issued to the authenticating client — so a user could never call it. These
// endpoints are the missing half. Withdrawal here is what Token.GrantPermitsIssuanceAsync and
// AccessTokens.ResolveAsync read.
//
// Withdrawal was all-or-nothing per application; superseded by D6/Q5 (I6): a single scope can be
// withdrawn too. That does not contradict RFC 6749 §6 (a rotated refresh token's scope must equal the
// presented one's), because narrowing is not "narrow and keep refreshing": OidcGrantWithdrawal sets
// LastRevokedAt, so every token issued before it dies and the client re-authorizes with what is left.
// Narrowing to nothing but openid is a withdrawal of the whole grant. The grant is marked revoked
// rather than deleted, so the audit trail survives and the issuance checks have a state to read.

/// <summary>Lists the applications the signed-in user has authorized (<c>GET /connect/applications</c>).</summary>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcConnectedApplications : IGetEndpoint<string>
{
    public static string Path => "/applications";

    /// <summary>The outcome of a withdrawal, carried back by its redirect.</summary>
    [QueryParam("status")] public string? Status { get; set; }

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IAntiforgery antiforgery;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ConnectText text;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var context = httpContextAccessor.HttpContext!;

        var userId = await context.GetInteractiveUserIdAsync();
        if (string.IsNullOrEmpty(userId))
        {
            var returnUrl = context.Request.Path + context.Request.QueryString;
            return Results.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        using var session = store.OpenAsyncSession();

        var grants = await LoadGrantsAsync(session, userId, ct);

        // Names for the rows. A grant whose application has been deleted still lists and still
        // withdraws — that is the grant a user is most likely to want gone, and dropping the row
        // would leave it un-withdrawable through the only surface that can withdraw it.
        var apps = await session.LoadAsync<OidcApplication>(
            grants.Select(g => g.ApplicationId).Distinct(), ct);
        var scopeDefinitions = await OidcScopeCatalog.LoadAsync(session, grants.SelectMany(g => g.GrantedScopes), ct);

        return ConnectResults.Html(RenderPage(context, grants, apps, scopeDefinitions, NoticeKey(Status)));
    }

    private static async Task<List<OidcGrant>> LoadGrantsAsync(
        IAsyncDocumentSession session, string userId, CancellationToken ct)
    {
        // Display only — see the index's own remarks. Status is filtered in memory rather than
        // as a query predicate so a stale index cannot decide what the user is shown.
        var all = await session
            .Query<OidcGrant, OidcGrants_BySubject>()
            .Where(a => a.Subject == userId, exact: true)
            .ToListAsync(ct);

        return [.. all.Where(a => a.Status == "valid").OrderBy(a => a.ApplicationId, StringComparer.Ordinal)];
    }

    /// <summary>The text key for a withdrawal outcome; any other value shows nothing.</summary>
    private static string? NoticeKey(string? status) => status switch
    {
        "revoked" => "applicationsRevoked",
        "scope_revoked" => "applicationsScopeRevoked",
        "failed" => "applicationsFailed",
        _ => null,
    };

    private string RenderPage(
        HttpContext context,
        List<OidcGrant> grants,
        Dictionary<string, OidcApplication> apps,
        List<OidcScopeDefinition> scopeDefinitions,
        string? noticeKey)
    {
        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, context, text["applicationsTitle"], text.Culture, options.Branding);
        sb.Append("body{max-width:560px;margin:60px auto;padding:0 20px}");
        sb.Append(".app{padding:16px 0;border-bottom:1px solid var(--idp-border);display:flex;align-items:flex-start;gap:16px}");
        sb.Append(".app-body{flex:1}.app-name{font-weight:600}");
        sb.Append(".scopes{list-style:none;padding:0;margin:6px 0 0;color:var(--idp-muted);font-size:13px}");
        sb.Append(".scopes li{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:2px 0}");
        sb.Append(".scopes form{margin:0}");
        sb.Append(".btn{padding:8px 16px;border:none;border-radius:6px;font-size:14px;cursor:pointer;background:var(--idp-danger);color:#fff}");
        sb.Append(".btn-scope{background:none;border:1px solid var(--idp-border);border-radius:4px;color:var(--idp-link);font-size:12px;padding:2px 8px;cursor:pointer}");
        sb.Append(".notice{background:var(--idp-notice-bg);color:var(--idp-notice-color);padding:10px 14px;border-radius:6px;margin-bottom:20px}");
        sb.Append(".empty{color:var(--idp-muted)}.footnote{color:var(--idp-muted);font-size:13px;margin-top:24px}");
        sb.Append("</style></head><body>");
        ConnectPageTheme.AppendBrand(sb, options.Branding);
        sb.Append("<h2>").Append(Encode(text["applicationsTitle"])).Append("</h2>");

        if (noticeKey != null)
            sb.Append("<div class=\"notice\">").Append(Encode(text[noticeKey])).Append("</div>");

        if (grants.Count == 0)
        {
            sb.Append("<p class=\"empty\">").Append(Encode(text["applicationsEmpty"])).Append("</p>");
        }
        else
        {
            foreach (var grant in grants)
            {
                // Fall back to the raw id when the application is gone, rather than hiding the row.
                var name = apps.TryGetValue(grant.ApplicationId, out var app) && app is not null
                    ? app.DisplayName
                    : grant.ApplicationId;

                sb.Append("<div class=\"app\"><div class=\"app-body\">");
                sb.Append("<div class=\"app-name\">").Append(Encode(name)).Append("</div>");

                if (grant.GrantedScopes.Count > 0)
                {
                    sb.Append("<ul class=\"scopes\">");
                    foreach (var scope in grant.GrantedScopes)
                    {
                        var def = scopeDefinitions.FirstOrDefault(d => string.Equals(d.Name, scope, StringComparison.OrdinalIgnoreCase));
                        var displayName = text.Of(def?.DisplayName, scope);
                        sb.Append("<li><span>").Append(Encode(displayName)).Append("</span>");

                        // openid is what the grant is; taking it away alone is "Remove access" below.
                        // Every other scope gets its own small form: the whole-grant form's fields plus
                        // the scope, with its own antiforgery token.
                        if (!string.Equals(scope, "openid", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.Append("<form method=\"post\" action=\"/connect/applications/revoke-scope\">");
                            AppendAntiforgery(sb, antiforgery, context);
                            AppendHidden(sb, "application_id", grant.ApplicationId);
                            AppendHidden(sb, "scope", scope);
                            sb.Append("<button type=\"submit\" class=\"btn-scope\" aria-label=\"")
                              .Append(Encode(text["applicationsRemoveScopeLabel", displayName])).Append("\">")
                              .Append(Encode(text["applicationsRemoveScope"])).Append("</button>");
                            sb.Append("</form>");
                        }
                        sb.Append("</li>");
                    }
                    sb.Append("</ul>");
                }

                sb.Append("</div>");
                sb.Append("<form method=\"post\" action=\"/connect/applications/revoke\">");
                AppendAntiforgery(sb, antiforgery, context);
                AppendHidden(sb, "application_id", grant.ApplicationId);
                sb.Append("<button type=\"submit\" class=\"btn\">").Append(Encode(text["applicationsRemove"])).Append("</button>");
                sb.Append("</form></div>");
            }

            // Said plainly rather than implied away. An access token is a signed JWT that a
            // resource server may check without ever asking us again, so we cannot recall one
            // already in flight — only stop new ones being issued. A page that implied otherwise
            // would be worse than no page.
            sb.Append("<p class=\"footnote\">").Append(Encode(text["applicationsFootnote"])).Append("</p>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }
}

/// <summary>The withdrawal form as posted: the application whose grant goes.</summary>
internal sealed record OidcRevokeApplicationRequest(string? ApplicationId);

/// <summary>Revokes one application's authorization (<c>POST /connect/applications/revoke</c>).</summary>
/// <remarks>
/// The body is the page's own HTML form, read by <see cref="BindRequestAsync"/>. Binding now runs
/// before the sign-in check, where the handler used to check first; for a form post the outcome is
/// the same (the form reads, then an anonymous caller gets 401).
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcRevokeApplication : IPostEndpoint<OidcRevokeApplicationRequest>
{
    public static string Path => "/applications/revoke";

    [Inject] private readonly OidcGrantWithdrawal withdrawal;
    [Inject] private readonly ConnectText text;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    /// <summary>Kept from <see cref="BindRequestAsync"/> (D8); the endpoint is created per request.</summary>
    private HttpContext context = null!;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    protected override async ValueTask<OidcRevokeApplicationRequest?> BindRequestAsync(HttpContext context)
    {
        this.context = context;
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcRevokeApplicationRequest(form["application_id"].FirstOrDefault());
    }

    public override async Task<IResult> HandleAsync(OidcRevokeApplicationRequest request, CancellationToken ct)
    {
        var userId = await context.GetInteractiveUserIdAsync();
        if (string.IsNullOrEmpty(userId))
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status401Unauthorized, "errorNotAuthenticated");

        var applicationId = request.ApplicationId;

        // The form names an *application*; the grant id is derived from the session's user. So
        // there is no parameter a forged post could set to reach someone else's grant — the
        // property is structural rather than a check that has to be remembered. It also means
        // "not yours" and "no such grant" are the same missing document here, so the response
        // cannot distinguish them and cannot be used to probe which grants exist.
        var withdrawn = string.IsNullOrEmpty(applicationId)
            || await withdrawal.WithdrawAsync(userId, applicationId, scopes: null, context.Connection.RemoteIpAddress?.ToString(), ct);

        // Reporting "Access removed" when the write lost a race would be the worst possible
        // outcome here: the user believes they have taken access back and has no reason to look
        // again. Saying so only when the write actually landed.
        return Results.Redirect(withdrawn
            ? "/connect/applications?status=revoked"
            : "/connect/applications?status=failed");
    }
}

/// <summary>The per-scope withdrawal form as posted: the application, and the one scope that goes.</summary>
internal sealed record OidcRevokeScopeRequest(string? ApplicationId, string? Scope);

/// <summary>
/// Withdraws one scope of one application's authorization (<c>POST /connect/applications/revoke-scope</c>,
/// PRD D6/Q5).
/// </summary>
/// <remarks>
/// The same shape and guarantees as <see cref="OidcRevokeApplication"/>: an antiforgery-stamped form read
/// by <see cref="BindRequestAsync"/>; the grant id derived from the session's user, so no field can reach
/// someone else's grant; success reported only when the write landed. The scope is only ever removed from
/// the caller's own grant, so a scope it does not hold is a no-op; withdrawing the last scope besides
/// <c>openid</c> withdraws the whole grant (<see cref="OidcGrantWithdrawal"/>).
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcRevokeApplicationScope : IPostEndpoint<OidcRevokeScopeRequest>
{
    public static string Path => "/applications/revoke-scope";

    [Inject] private readonly OidcGrantWithdrawal withdrawal;
    [Inject] private readonly ConnectText text;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    /// <summary>Kept from <see cref="BindRequestAsync"/> (D8); the endpoint is created per request.</summary>
    private HttpContext context = null!;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    protected override async ValueTask<OidcRevokeScopeRequest?> BindRequestAsync(HttpContext context)
    {
        this.context = context;
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcRevokeScopeRequest(form["application_id"].FirstOrDefault(), form["scope"].FirstOrDefault());
    }

    public override async Task<IResult> HandleAsync(OidcRevokeScopeRequest request, CancellationToken ct)
    {
        var userId = await context.GetInteractiveUserIdAsync();
        if (string.IsNullOrEmpty(userId))
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status401Unauthorized, "errorNotAuthenticated");

        var applicationId = request.ApplicationId;
        var scope = request.Scope;

        // As the whole-grant form: a form that names nothing withdraws nothing, and reports success.
        var withdrawn = string.IsNullOrEmpty(applicationId) || string.IsNullOrEmpty(scope)
            || await withdrawal.WithdrawAsync(userId, applicationId, [scope], context.Connection.RemoteIpAddress?.ToString(), ct);

        // Only when the write landed, for the reason OidcRevokeApplication gives.
        return Results.Redirect(withdrawn
            ? "/connect/applications?status=scope_revoked"
            : "/connect/applications?status=failed");
    }
}
