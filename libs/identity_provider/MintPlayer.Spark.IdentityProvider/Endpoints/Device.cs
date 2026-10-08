using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using static MintPlayer.Spark.IdentityProvider.Endpoints.ConnectPage;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// <c>/connect/device</c> (RFC 8628 §3.3, <c>docs/identity_provider_platform_PRD.md</c> D7): the signed-in
/// person types the code their device shows, sees which application asks for what, and allows or denies
/// it. Server-rendered, like every page the sign-in flow shows.
/// </summary>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcDevicePage : IGetEndpoint<string>
{
    public static string Path => "/device";

    [QueryParam("user_code")] public string? UserCode { get; set; }
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
        OidcToken? device = null;
        OidcApplication? app = null;
        List<OidcScopeDefinition> scopes = [];
        if (!string.IsNullOrWhiteSpace(UserCode))
        {
            device = await OidcDeviceCodes.FindByUserCodeAsync(session, UserCode, ct);
            if (device is not null)
            {
                app = await session.LoadAsync<OidcApplication>(device.ApplicationId, ct);
                scopes = await OidcScopeCatalog.LoadAsync(session, device.Scopes, ct);
            }
        }

        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, context, text["deviceTitle"], text.Culture, options.Branding);
        sb.Append("body{max-width:440px;margin:60px auto;padding:0 20px}");
        sb.Append("input[type=text]{width:100%;padding:10px 12px;border:1px solid var(--idp-input-border);border-radius:6px;font-size:20px;letter-spacing:4px;text-transform:uppercase;box-sizing:border-box}");
        sb.Append(".btn{padding:10px 24px;border:none;border-radius:6px;font-size:14px;cursor:pointer;margin-top:16px}");
        sb.Append(".btn-allow{background:var(--idp-primary);color:#fff}.btn-deny{background:var(--idp-secondary);color:#fff;margin-left:8px}");
        sb.Append(".notice{padding:10px;border-radius:6px;background:var(--idp-notice-bg);color:var(--idp-notice-color)}");
        sb.Append(".error{padding:10px;border-radius:6px;background:var(--idp-error-bg);color:var(--idp-error-color)}");
        sb.Append("</style></head><body>");
        ConnectPageTheme.AppendBrand(sb, options.Branding);
        sb.Append("<h2>").Append(Encode(text["deviceTitle"])).Append("</h2>");

        if (Status is "allowed" or "denied")
            sb.Append("<p class=\"notice\">").Append(Encode(text[Status == "allowed" ? "deviceAllowed" : "deviceDenied"])).Append("</p>");

        if (device is null || app is null)
        {
            if (!string.IsNullOrWhiteSpace(UserCode))
                sb.Append("<p class=\"error\">").Append(Encode(text["deviceUnknownCode"])).Append("</p>");
            sb.Append("<form method=\"get\"><label for=\"user_code\">").Append(Encode(text["deviceEnterCode"])).Append("</label>");
            sb.Append("<input type=\"text\" id=\"user_code\" name=\"user_code\" autocomplete=\"off\" required autofocus />");
            sb.Append("<button type=\"submit\" class=\"btn btn-allow\">").Append(Encode(text["deviceContinue"])).Append("</button></form>");
        }
        else
        {
            sb.Append("<p>").Append(Encode(text["deviceAsks", app.DisplayName])).Append("</p><ul>");
            foreach (var scope in scopes)
                sb.Append("<li>").Append(Encode(text.Of(scope.DisplayName, scope.Name))).Append("</li>");
            sb.Append("</ul><form method=\"post\">");
            AppendAntiforgery(sb, antiforgery, context);
            AppendHidden(sb, "user_code", UserCode);
            sb.Append("<button type=\"submit\" name=\"decision\" value=\"allow\" class=\"btn btn-allow\">").Append(Encode(text["consentAllow"])).Append("</button>");
            sb.Append("<button type=\"submit\" name=\"decision\" value=\"deny\" class=\"btn btn-deny\">").Append(Encode(text["consentDeny"])).Append("</button></form>");
        }

        sb.Append("</body></html>");
        return ConnectResults.Html(sb.ToString());
    }
}

internal sealed record OidcDeviceDecision(string? UserCode, string? Decision);

/// <summary>Records the person's decision on a device authorization (RFC 8628 §3.3).</summary>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcDeviceSubmit : IPostEndpoint<OidcDeviceDecision>
{
    public static string Path => "/device";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcAudit audit;

    private HttpContext context = null!;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    protected override async ValueTask<OidcDeviceDecision?> BindRequestAsync(HttpContext context)
    {
        this.context = context;
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcDeviceDecision(form["user_code"].FirstOrDefault(), form["decision"].FirstOrDefault());
    }

    public override async Task<IResult> HandleAsync(OidcDeviceDecision request, CancellationToken ct)
    {
        var signedIn = await OidcInteractiveSession.ReadAsync(context);
        if (signedIn is null)
            return Results.Redirect("/connect/device");

        using var session = store.OpenAsyncSession();
        var device = string.IsNullOrEmpty(request.UserCode) ? null : await OidcDeviceCodes.FindByUserCodeAsync(session, request.UserCode, ct);
        var app = device is null ? null : await session.LoadAsync<OidcApplication>(device.ApplicationId, ct);
        if (device is null || app is null)
            return Results.Redirect($"/connect/device?user_code={Uri.EscapeDataString(request.UserCode ?? "")}");

        if (request.Decision != "allow" || !OidcApplicationAccess.MayAuthorize(app, signedIn.UserId))
        {
            device.Status = "denied";
            await session.SaveChangesAsync(ct);
            return Results.Redirect("/connect/device?status=denied");
        }

        var scopes = OidcApplicationAccess.AvailableScopes(app, signedIn.UserId, device.Scopes);
        device.Status = "approved";
        device.Subject = signedIn.UserId;
        device.Scopes = scopes;
        device.AuthTime = signedIn.AuthTime;
        device.Properties["amr"] = string.Join(' ', signedIn.Amr);
        device.Properties["acr"] = signedIn.Acr;
        if (signedIn.SessionId is { } sid) device.Properties["sid"] = sid;
        device.AuthorizationId = await OidcAuthorizationFlow.EnsureAuthorizationAsync(session, app, signedIn.UserId, scopes, remember: false, ct);
        await audit.RecordAsync(session, OidcAuditKinds.ConsentGranted, signedIn.UserId, app.Id, signedIn.UserId,
            context.Connection.RemoteIpAddress?.ToString(), new Dictionary<string, string> { ["scopes"] = string.Join(' ', scopes), ["device"] = "true" }, ct);
        await session.SaveChangesAsync(ct);
        return Results.Redirect("/connect/device?status=allowed");
    }
}
