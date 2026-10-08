using ExternalLoginErrors = MintPlayer.Spark.Authorization.Extensions.SparkAuthenticationExtensions.ExternalLoginErrors;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.Pages;

namespace MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;

/// <summary>
/// The application's second factor after an external sign-in (#490 D11): the switches, resolved once per request.
/// </summary>
internal static class ExternalLoginTwoFactor
{
    public const string PagePath = "/spark/auth/external-login/two-factor";

    /// <summary>Configuration (<c>Spark:Auth:ExternalLogin:TwoFactor:*</c>) wins over the code options when set.</summary>
    public static SparkExternalLoginTwoFactorOptions Resolve(IServiceProvider services)
    {
        var configured = services.GetRequiredService<IOptions<SparkAuthenticationOptions>>().Value.ExternalLoginTwoFactor;
        var section = services.GetService<IConfiguration>()?.GetSection("Spark:Auth:ExternalLogin:TwoFactor");
        return new SparkExternalLoginTwoFactorOptions
        {
            Enabled = bool.TryParse(section?["Enabled"], out var enabled) ? enabled : configured.Enabled,
            AllowUserBypass = bool.TryParse(section?["AllowUserBypass"], out var bypass) ? bypass : configured.AllowUserBypass,
        };
    }

    /// <summary>
    /// The page's URL, carrying the callback's hand-off flags (popup, nonce) and the return URL. <c>ngsw-bypass</c>
    /// keeps a service worker's navigation fallback off it (#464 D8).
    /// </summary>
    public static string Url(HttpContext context, bool popup, string? nonce, string returnUrl, string? error = null, bool recovery = false)
    {
        var query = new Dictionary<string, string?> { ["returnUrl"] = returnUrl, ["ngsw-bypass"] = "true" };
        if (popup) query["popup"] = "true";
        if (nonce is not null) query[SparkExternalLoginNonce.QueryParameter] = nonce;
        if (error is not null) query["error"] = error;
        if (recovery) query["recovery"] = "true";
        return QueryHelpers.AddQueryString(context.Request.PathBase + PagePath, query);
    }

    /// <summary>The page's language: the SPA's <c>spark-lang</c> cookie when it is a culture name, else the request's.</summary>
    public static string Text(IManager manager, HttpContext context, string key)
    {
        var cookie = context.Request.Cookies["spark-lang"];
        return cookie is not null && CultureName().IsMatch(cookie)
            ? manager.GetMessage("auth." + key, cookie)
            : manager.GetTranslatedMessage("auth." + key);
    }

    public static string? Culture(HttpContext context)
        => context.Request.Cookies["spark-lang"] is { } cookie && CultureName().IsMatch(cookie) ? cookie : null;

    private static readonly Regex CultureNameRegex = new("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$", RegexOptions.CultureInvariant);
    private static Regex CultureName() => CultureNameRegex;
}

/// <summary>
/// Renders the two-factor step of an external sign-in (<c>GET /spark/auth/external-login/two-factor</c>, #490 D11).
/// </summary>
/// <remarks>
/// <para>
/// Server-rendered, because anything visible in the sign-in popup is (IdP PRD Q7), with the identity provider's
/// renderer (<see cref="ConnectPageTheme"/>). Reached only from <see cref="ExternalLoginCallback{TUser}"/> once
/// <c>ExternalLoginSignInAsync</c> answered <c>RequiresTwoFactor</c>, which set Identity's two-factor cookie; without
/// that cookie the page ends the flow with <c>requires_two_factor</c>, as the callback did before D11.
/// </para>
/// <para>Error codes are fixed keys, never the query text itself.</para>
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ExternalLoginTwoFactorPage<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/external-login/two-factor";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.AllowAnonymous();

    [QueryParam] public string? ReturnUrl { get; set; }
    [QueryParam] public string? Error { get; set; }
    [QueryParam] public string? Recovery { get; set; }

    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly IAntiforgery antiforgery;
    [Inject] private readonly IManager manager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var returnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(ReturnUrl);
        var popup = httpContext.Request.Query.ContainsKey("popup");
        var nonce = SparkExternalLoginNonce.Accept(httpContext.Request.Query[SparkExternalLoginNonce.QueryParameter]);

        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
            return SparkAuthenticationExtensions.ExternalLoginOutcome(popup, nonce, returnUrl, ExternalLoginErrors.RequiresTwoFactor);

        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Content(BuildHtml(httpContext, returnUrl, popup, nonce, Recovery == "true"), "text/html; charset=utf-8");
    }

    private string BuildHtml(HttpContext context, string returnUrl, bool popup, string? nonce, bool useRecoveryCode)
    {
        string T(string key) => ExternalLoginTwoFactor.Text(manager, context, key);
        var errorKey = Error switch
        {
            "missing_code" or "invalid_code" => "externalTwoFactorInvalidCode",
            "missing_recovery_code" or "invalid_recovery_code" => "externalTwoFactorInvalidRecoveryCode",
            _ => null,
        };

        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, context, T("externalTwoFactorTitle"), ExternalLoginTwoFactor.Culture(context));
        sb.Append("body{max-width:400px;margin:80px auto;padding:0 20px}");
        sb.Append("h2{margin-bottom:24px}");
        sb.Append(".form-group{margin-bottom:16px}");
        sb.Append("label{display:block;margin-bottom:4px;font-weight:500;font-size:14px}");
        sb.Append("input[type=text]{width:100%;padding:8px 12px;border:1px solid var(--idp-input-border);border-radius:6px;font-size:14px;box-sizing:border-box}");
        sb.Append("input[type=text]:focus{border-color:var(--idp-focus-border);outline:0;box-shadow:0 0 0 .25rem var(--idp-focus-ring)}");
        sb.Append(".check{display:flex;gap:8px;align-items:center;font-size:14px;margin-bottom:16px}");
        sb.Append(".btn{display:block;width:100%;padding:10px;border:none;border-radius:6px;font-size:14px;cursor:pointer;box-sizing:border-box}");
        sb.Append(".btn-primary{background:var(--idp-primary);color:#fff;margin-top:8px}");
        sb.Append(".btn-primary:hover{background:var(--idp-primary-hover)}");
        sb.Append(".btn-link{color:var(--idp-link);font-size:14px;text-decoration:underline;margin-top:12px;display:inline-block}");
        sb.Append(".error{color:var(--idp-error-color);background:var(--idp-error-bg);border:1px solid var(--idp-error-border);padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append(".info{color:var(--idp-info-color);background:var(--idp-info-bg);border:1px solid var(--idp-info-border);padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append("</style></head><body>");
        sb.Append("<h2>").Append(SparkPageHtml.Encode(T("externalTwoFactorTitle"))).Append("</h2>");
        if (errorKey is not null)
            sb.Append("<div class=\"error\">").Append(SparkPageHtml.Encode(T(errorKey))).Append("</div>");

        sb.Append("<form method=\"post\">");
        SparkPageHtml.AppendAntiforgery(sb, antiforgery, context);
        SparkPageHtml.AppendHidden(sb, "returnUrl", returnUrl);
        if (popup) SparkPageHtml.AppendHidden(sb, "popup", "true");
        if (nonce is not null) SparkPageHtml.AppendHidden(sb, SparkExternalLoginNonce.QueryParameter, nonce);

        if (useRecoveryCode)
        {
            sb.Append("<div class=\"info\">").Append(SparkPageHtml.Encode(T("externalTwoFactorRecoveryInfo"))).Append("</div>");
            SparkPageHtml.AppendHidden(sb, "useRecoveryCode", "true");
            sb.Append("<div class=\"form-group\"><label for=\"recoveryCode\">").Append(SparkPageHtml.Encode(T("externalTwoFactorRecoveryLabel"))).Append("</label>");
            sb.Append("<input type=\"text\" id=\"recoveryCode\" name=\"recoveryCode\" required autofocus autocomplete=\"off\" /></div>");
        }
        else
        {
            sb.Append("<div class=\"info\">").Append(SparkPageHtml.Encode(T("externalTwoFactorCodeInfo"))).Append("</div>");
            sb.Append("<div class=\"form-group\"><label for=\"code\">").Append(SparkPageHtml.Encode(T("externalTwoFactorCodeLabel"))).Append("</label>");
            sb.Append("<input type=\"text\" id=\"code\" name=\"code\" required autofocus autocomplete=\"one-time-code\" inputmode=\"numeric\" pattern=\"[0-9 ]*\" maxlength=\"7\" /></div>");
            // Identity's own "remember this browser" (TwoFactorRememberMe): the next external sign-in from it skips the step.
            sb.Append("<label class=\"check\"><input type=\"checkbox\" name=\"rememberBrowser\" value=\"true\" />")
              .Append(SparkPageHtml.Encode(T("externalTwoFactorRememberBrowser"))).Append("</label>");
        }
        sb.Append("<button type=\"submit\" class=\"btn btn-primary\">").Append(SparkPageHtml.Encode(T("externalTwoFactorVerify"))).Append("</button>");
        sb.Append("</form>");

        var switchUrl = ExternalLoginTwoFactor.Url(context, popup, nonce, returnUrl, recovery: !useRecoveryCode);
        sb.Append("<a class=\"btn-link\" href=\"").Append(SparkPageHtml.Encode(switchUrl)).Append("\">")
          .Append(SparkPageHtml.Encode(T(useRecoveryCode ? "externalTwoFactorUseAuthenticator" : "externalTwoFactorUseRecovery"))).Append("</a>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}

/// <summary>The two-factor form as posted.</summary>
internal sealed record ExternalLoginTwoFactorSubmission(
    string? Code, string? RecoveryCode, bool UseRecoveryCode, bool RememberBrowser, string? ReturnUrl, bool Popup, string? Nonce);

/// <summary>
/// Accepts the two-factor step of an external sign-in (<c>POST /spark/auth/external-login/two-factor</c>, #490 D11).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Success ends exactly like a direct callback: the popup hand-off page (nonce, BroadcastChannel,
/// localStorage, opener) in popup mode, a redirect to the return URL otherwise.</item>
/// <item>A wrong code stays on the page, and Identity counts it towards lockout. A lockout ends the flow with
/// <c>locked_out</c>; an expired or missing two-factor cookie with <c>requires_two_factor</c>.</item>
/// </list>
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ExternalLoginTwoFactorSubmit<TUser> : IPostEndpoint<ExternalLoginTwoFactorSubmission>
    where TUser : SparkUser, new()
{
    public static string Path => "/external-login/two-factor";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.AllowAnonymous().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IAntiforgery antiforgery;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override async ValueTask<ExternalLoginTwoFactorSubmission?> BindRequestAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new ExternalLoginTwoFactorSubmission(
            Code: form["code"].FirstOrDefault()?.Replace(" ", "").Replace("-", ""),
            RecoveryCode: form["recoveryCode"].FirstOrDefault()?.Replace(" ", ""),
            UseRecoveryCode: form["useRecoveryCode"].FirstOrDefault() == "true",
            RememberBrowser: form["rememberBrowser"].FirstOrDefault() == "true",
            ReturnUrl: form["returnUrl"].FirstOrDefault(),
            Popup: form["popup"].FirstOrDefault() == "true",
            Nonce: SparkExternalLoginNonce.Accept(form[SparkExternalLoginNonce.QueryParameter]));
    }

    public override async Task<IResult> HandleAsync(ExternalLoginTwoFactorSubmission submission, CancellationToken ct)
    {
        var context = httpContextAccessor.HttpContext!;
        var returnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(submission.ReturnUrl);

        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is not { } user)
            return SparkAuthenticationExtensions.ExternalLoginOutcome(submission.Popup, submission.Nonce, returnUrl, ExternalLoginErrors.RequiresTwoFactor);

        if (submission.UseRecoveryCode ? string.IsNullOrEmpty(submission.RecoveryCode) : string.IsNullOrEmpty(submission.Code))
            return Back(context, submission, returnUrl, submission.UseRecoveryCode ? "missing_recovery_code" : "missing_code");

        var result = submission.UseRecoveryCode
            ? await signInManager.TwoFactorRecoveryCodeSignInAsync(submission.RecoveryCode!)
            // isPersistent as the callback signs in (true); rememberClient is Identity's "remember this browser".
            : await signInManager.TwoFactorAuthenticatorSignInAsync(submission.Code!, isPersistent: true, rememberClient: submission.RememberBrowser);

        if (result.IsLockedOut)
            return SparkAuthenticationExtensions.ExternalLoginOutcome(submission.Popup, submission.Nonce, returnUrl, ExternalLoginErrors.LockedOut);
        if (!result.Succeeded)
            return Back(context, submission, returnUrl, submission.UseRecoveryCode ? "invalid_recovery_code" : "invalid_code");

        // What the callback does after a direct sign-in: keep the provider's tokens for later API use.
        if (await signInManager.GetExternalLoginInfoAsync() is { AuthenticationTokens: { } tokens } info)
        {
            foreach (var token in tokens)
                await userManager.SetAuthenticationTokenAsync(user, info.LoginProvider, token.Name, token.Value);
        }

        antiforgery.GetAndStoreTokens(context);
        return SparkAuthenticationExtensions.ExternalLoginOutcome(submission.Popup, submission.Nonce, returnUrl, error: null);
    }

    private static IResult Back(HttpContext context, ExternalLoginTwoFactorSubmission submission, string returnUrl, string error)
        => Results.Redirect(ExternalLoginTwoFactor.Url(context, submission.Popup, submission.Nonce, returnUrl, error, submission.UseRecoveryCode));
}
