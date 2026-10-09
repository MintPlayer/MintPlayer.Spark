using MintPlayer.Spark.Authorization.Extensions;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

// The two-factor step of the provider's own sign-in: GET renders the code entry form, POST
// verifies and redirects to returnUrl. Membership of OidcLocalCredentialsGroup is what gates both
// on SparkLocalCredentials - see that group's IsEnabled.

/// <summary>Renders the two-factor step (<c>GET /connect/two-factor</c>).</summary>
[MemberOf<OidcLocalCredentialsGroup>]
internal sealed partial class OidcTwoFactorPage : IGetEndpoint<string>
{
    public static string Path => "/two-factor";

    [QueryParam("returnUrl")] public string? ReturnUrl { get; set; }
    [QueryParam("error")] public string? Error { get; set; }
    /// <summary><c>true</c> shows the recovery-code form; anything else the authenticator form.</summary>
    [QueryParam("recovery")] public string? Recovery { get; set; }
    /// <summary>The login page's remember-me choice, carried across the hop.</summary>
    [QueryParam("rememberMe")] public string? RememberMe { get; set; }

    [Inject] private readonly IAntiforgery antiforgery;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ConnectText text;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        // Sanitized at the point of read: an unvalidated returnUrl sends a freshly-authenticated
        // user off-origin, which is high-value phishing precisely because they really did just
        // authenticate here. Shared with the Authorization package rather than duplicated.
        var returnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(ReturnUrl);
        var useRecoveryCode = Recovery == "true";
        var rememberMe = RememberMe == "true";
        // D7: the pending authorize request's ui_locales, when that is what this sign-in resumes.
        text.UseUiLocalesOfReturnUrl(returnUrl);

        return Task.FromResult(ConnectResults.Html(
            BuildFormHtml(httpContextAccessor.HttpContext!, returnUrl, Error, useRecoveryCode, rememberMe)));
    }

    private string BuildFormHtml(HttpContext context, string returnUrl, string? error, bool useRecoveryCode, bool rememberMe)
    {
        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, context, text["twoFactorTitle"], text.Culture, options.Branding);
        sb.Append("body{max-width:400px;margin:80px auto;padding:0 20px}");
        sb.Append("h2{margin-bottom:24px}");
        sb.Append(".form-group{margin-bottom:16px}");
        sb.Append("label{display:block;margin-bottom:4px;font-weight:500;font-size:14px}");
        sb.Append("input[type=text]{width:100%;padding:8px 12px;border:1px solid var(--idp-input-border);border-radius:6px;font-size:14px;box-sizing:border-box}");
        sb.Append("input[type=text]:focus{border-color:var(--idp-focus-border);outline:0;box-shadow:0 0 0 .25rem var(--idp-focus-ring)}");
        sb.Append(".btn{display:block;width:100%;padding:10px;border:none;border-radius:6px;font-size:14px;cursor:pointer;box-sizing:border-box}");
        sb.Append(".btn-primary{background:var(--idp-primary);color:#fff;margin-top:8px}");
        sb.Append(".btn-primary:hover{background:var(--idp-primary-hover)}");
        sb.Append(".btn-link{background:none;border:none;color:var(--idp-link);cursor:pointer;padding:0;font-size:14px;text-decoration:underline;margin-top:12px;display:inline-block}");
        sb.Append(".error{color:var(--idp-error-color);background:var(--idp-error-bg);border:1px solid var(--idp-error-border);padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append(".info{color:var(--idp-info-color);background:var(--idp-info-bg);border:1px solid var(--idp-info-border);padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append("</style></head><body>");
        ConnectPageTheme.AppendBrand(sb, options.Branding);
        sb.Append("<h2>").Append(ConnectPage.Encode(text["twoFactorTitle"])).Append("</h2>");

        // A fixed message per code, never the query text itself (see ConnectPage.ErrorKey).
        if (ConnectPage.ErrorKey(error) is { } errorKey)
        {
            sb.Append("<div class=\"error\">").Append(ConnectPage.Encode(text[errorKey])).Append("</div>");
        }

        sb.Append("<form method=\"post\">");
        ConnectPage.AppendAntiforgery(sb, antiforgery, context);
        sb.Append("<input type=\"hidden\" name=\"returnUrl\" value=\"").Append(ConnectPage.Encode(returnUrl)).Append("\" />");
        if (rememberMe)
            sb.Append("<input type=\"hidden\" name=\"rememberMe\" value=\"true\" />");

        if (useRecoveryCode)
        {
            sb.Append("<div class=\"info\">").Append(ConnectPage.Encode(text["twoFactorRecoveryInfo"])).Append("</div>");
            sb.Append("<input type=\"hidden\" name=\"useRecoveryCode\" value=\"true\" />");
            sb.Append("<div class=\"form-group\">");
            sb.Append("<label for=\"recoveryCode\">").Append(ConnectPage.Encode(text["twoFactorRecoveryLabel"])).Append("</label>");
            sb.Append("<input type=\"text\" id=\"recoveryCode\" name=\"recoveryCode\" required autofocus autocomplete=\"off\" />");
            sb.Append("</div>");
            sb.Append("<button type=\"submit\" class=\"btn btn-primary\">").Append(ConnectPage.Encode(text["twoFactorVerify"])).Append("</button>");
            sb.Append("</form>");
            sb.Append("<a href=\"/connect/two-factor?returnUrl=").Append(Uri.EscapeDataString(returnUrl)).Append(rememberMe ? "&rememberMe=true" : "").Append("\" class=\"btn-link\">")
              .Append(ConnectPage.Encode(text["twoFactorUseAuthenticator"])).Append("</a>");
        }
        else
        {
            sb.Append("<div class=\"info\">").Append(ConnectPage.Encode(text["twoFactorCodeInfo"])).Append("</div>");
            sb.Append("<div class=\"form-group\">");
            sb.Append("<label for=\"code\">").Append(ConnectPage.Encode(text["twoFactorCodeLabel"])).Append("</label>");
            sb.Append("<input type=\"text\" id=\"code\" name=\"code\" required autofocus autocomplete=\"one-time-code\" inputmode=\"numeric\" pattern=\"[0-9]*\" maxlength=\"6\" />");
            sb.Append("</div>");
            sb.Append("<button type=\"submit\" class=\"btn btn-primary\">").Append(ConnectPage.Encode(text["twoFactorVerify"])).Append("</button>");
            sb.Append("</form>");
            sb.Append("<a href=\"/connect/two-factor?returnUrl=").Append(Uri.EscapeDataString(returnUrl)).Append("&recovery=true").Append(rememberMe ? "&rememberMe=true" : "").Append("\" class=\"btn-link\">")
              .Append(ConnectPage.Encode(text["twoFactorUseRecovery"])).Append("</a>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }
}

/// <summary>The two-factor form as posted.</summary>
internal sealed record OidcTwoFactorSubmission(
    string? Code,
    string? RecoveryCode,
    bool UseRecoveryCode,
    string? ReturnUrl,
    bool RememberMe);

/// <summary>Accepts the two-factor step (<c>POST /connect/two-factor</c>).</summary>
/// <remarks>
/// <para>
/// Generic over the application's user type, closed once when the routes are mapped
/// (<see cref="OidcUserEndpoints"/>), so it signs in through a typed <see cref="SignInManager{TUser}"/>
/// instead of resolving and reflecting on one per request. The request-time "Identity not
/// configured" 500 is gone: startup now refuses that configuration.
/// </para>
/// <para>
/// ⚠️ The antiforgery stamp is explicit: the form is read by <see cref="BindRequestAsync"/>, not by
/// a <c>[FromForm]</c> parameter, so nothing infers the metadata for it.
/// </para>
/// </remarks>
[MemberOf<OidcLocalCredentialsGroup>]
internal sealed partial class OidcTwoFactorSubmit<TUser> : IPostEndpoint<OidcTwoFactorSubmission>
    where TUser : SparkUser, new()
{
    public static string Path => "/two-factor";

    [Inject] private readonly SignInManager<TUser> signInManager;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    protected override async ValueTask<OidcTwoFactorSubmission?> BindRequestAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcTwoFactorSubmission(
            Code: form["code"].FirstOrDefault()?.Replace(" ", "").Replace("-", ""),
            RecoveryCode: form["recoveryCode"].FirstOrDefault()?.Replace(" ", ""),
            UseRecoveryCode: form["useRecoveryCode"].FirstOrDefault() == "true",
            ReturnUrl: form["returnUrl"].FirstOrDefault(),
            // Carried across the hop as a hidden field, the same way returnUrl is. The choice is made
            // on the login page and spent here, so without threading it the second factor silently
            // overrode it — a user who declined a persistent cookie got one anyway.
            RememberMe: string.Equals(form["rememberMe"].FirstOrDefault(), "true", StringComparison.Ordinal));
    }

    public override async Task<IResult> HandleAsync(OidcTwoFactorSubmission submission, CancellationToken ct)
    {
        var returnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(submission.ReturnUrl);

        if (submission.UseRecoveryCode && string.IsNullOrEmpty(submission.RecoveryCode))
            return RedirectWithError(returnUrl, "missing_recovery_code", recovery: true);

        if (!submission.UseRecoveryCode && string.IsNullOrEmpty(submission.Code))
            return RedirectWithError(returnUrl, "missing_code");

        if (submission.UseRecoveryCode)
        {
            var result = await signInManager.TwoFactorRecoveryCodeSignInAsync(submission.RecoveryCode!);
            return result.Succeeded
                ? Results.Redirect(returnUrl)
                : RedirectWithError(returnUrl, "invalid_recovery_code", recovery: true);
        }
        else
        {
            var result = await signInManager.TwoFactorAuthenticatorSignInAsync(submission.Code!, submission.RememberMe, rememberClient: false);
            return result.Succeeded
                ? Results.Redirect(returnUrl)
                : RedirectWithError(returnUrl, "invalid_code");
        }
    }

    private static IResult RedirectWithError(string returnUrl, string error, bool recovery = false)
    {
        var url = $"/connect/two-factor?returnUrl={Uri.EscapeDataString(returnUrl)}&error={Uri.EscapeDataString(error)}";
        if (recovery) url += "&recovery=true";
        return Results.Redirect(url);
    }
}
