using MintPlayer.Spark.Authorization.Extensions;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

// The login page for the OIDC authorization flow: GET renders the sign-in choices, POST authenticates
// a password and redirects to returnUrl. The POST is a member of OidcLocalCredentialsGroup, which
// gates it on SparkLocalCredentials (see that group's IsEnabled); the GET is not, because with local
// credentials Disabled the page still has to offer the external providers (#490 M6, D7).

/// <summary>
/// Renders the provider's sign-in page: the password form unless local credentials are Disabled, and a
/// button per interactive external scheme (identity-provider federation).
/// </summary>
/// <remarks>
/// <para>
/// The external buttons are the same list <c>/spark/auth/capabilities</c> publishes
/// (<c>ExternalAuthenticationSchemes.GetInteractiveAsync</c>). Each is a plain link to
/// <c>/spark/auth/external-login</c> in <b>redirect</b> mode — no popup, this page has no script — with
/// <c>returnUrl</c> = the pending <c>/connect/authorize</c> URL, so a successful sign-in resumes the
/// authorization, and <c>errorUrl</c> = this page with the same <c>returnUrl</c>, so a refusal comes
/// back here as <c>?sparkExternalLogin=&lt;code&gt;</c> and is shown.
/// </para>
/// <para>
/// ⚠️ Every value is HTML-encoded on output and every URL part escaped when built; the scheme names and
/// display names come from the host's configuration, the return URL from the request.
/// </para>
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcLoginPage : IGetEndpoint<string>
{
    public static string Path => "/login";

    [QueryParam("returnUrl")] public string? ReturnUrl { get; set; }
    [QueryParam("error")] public string? Error { get; set; }
    [QueryParam("sparkExternalLogin")] public string? ExternalLoginError { get; set; }
    [QueryParam("login_hint")] public string? LoginHint { get; set; }

    [Inject] private readonly IAntiforgery antiforgery;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> authenticationOptions;
    // The concrete singleton, the same source OidcLocalCredentialsGroup.IsEnabled gates the POST on
    // (LocalCredentialsOf), so the form is shown exactly when its submit route is mapped.
    [Inject] private readonly SparkAuthenticationOptions sparkAuthenticationOptions;
    [Inject] private readonly IAuthenticationSchemeProvider schemes;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ConnectText text;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var httpContext = httpContextAccessor.HttpContext!;

        // Sanitized at the point of read: an unvalidated returnUrl sends a freshly-authenticated
        // user off-origin, which is high-value phishing precisely because they really did just
        // authenticate here. Shared with the Authorization package rather than duplicated.
        var returnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(ReturnUrl);
        var error = Error;
        var showPasswordForm = sparkAuthenticationOptions.LocalCredentials != SparkLocalCredentials.Disabled;
        var externalSchemes = await ExternalAuthenticationSchemes.GetInteractiveAsync(schemes);
        // D7: the pending authorize request's ui_locales, when that is what this sign-in resumes.
        text.UseUiLocalesOfReturnUrl(returnUrl);

        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, httpContext, text["loginTitle"], text.Culture, options.Branding);
        sb.Append("body{max-width:400px;margin:80px auto;padding:0 20px}");
        sb.Append("h2{margin-bottom:24px}");
        sb.Append(".form-group{margin-bottom:16px}");
        sb.Append("label{display:block;margin-bottom:4px;font-weight:500;font-size:14px}");
        sb.Append("input[type=text],input[type=email],input[type=password]{width:100%;padding:8px 12px;border:1px solid var(--idp-input-border);border-radius:6px;font-size:14px;box-sizing:border-box}");
        sb.Append("input[type=text]:focus,input[type=email]:focus,input[type=password]:focus{border-color:var(--idp-focus-border);outline:0;box-shadow:0 0 0 .25rem var(--idp-focus-ring)}");
        sb.Append(".btn{display:block;width:100%;padding:10px;border:none;border-radius:6px;font-size:14px;cursor:pointer;box-sizing:border-box}");
        sb.Append(".btn-primary{background:var(--idp-primary);color:#fff;margin-top:8px}");
        sb.Append(".btn-primary:hover{background:var(--idp-primary-hover)}");
        sb.Append(".error{color:var(--idp-error-color);background:var(--idp-error-bg);border:1px solid var(--idp-error-border);padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append(".notice{color:var(--idp-info-color);background:var(--idp-info-bg);border:1px solid var(--idp-info-border);padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append(".btn-external{background:var(--idp-input-bg);color:var(--idp-color);border:1px solid var(--idp-input-border);margin-top:8px;text-align:center;text-decoration:none}");
        sb.Append(".btn-external:hover{border-color:var(--idp-focus-border)}");
        sb.Append(".separator{display:flex;align-items:center;gap:8px;margin:20px 0 4px;color:var(--idp-muted);font-size:13px}");
        sb.Append(".separator::before,.separator::after{content:\"\";flex:1;border-top:1px solid var(--idp-border)}");
        sb.Append("</style></head><body>");
        ConnectPageTheme.AppendBrand(sb, options.Branding);
        sb.Append("<h2>").Append(Encode(text["loginHeading"])).Append("</h2>");

        if (ConnectPage.ErrorKey(error) is { } errorKey)
        {
            sb.Append("<div class=\"error\">").Append(Encode(text[errorKey])).Append("</div>");
        }

        // A refused external sign-in, as a fixed message per code (never the query text itself, for
        // the reason ConnectPage.ErrorKey gives). Two codes are not failures but "check your mail".
        if (ConnectPage.ExternalLoginKey(ExternalLoginError) is { } externalKey)
        {
            var cssClass = ConnectPage.IsExternalLoginNotice(ExternalLoginError) ? "notice" : "error";
            sb.Append("<div class=\"").Append(cssClass).Append("\" role=\"alert\">").Append(Encode(text[externalKey])).Append("</div>");
        }

        if (!showPasswordForm && externalSchemes.Count == 0)
        {
            // Not a 404: the route exists, the host simply offers nothing to sign in with. Saying so is
            // what lets an operator find the misconfiguration.
            sb.Append("<div class=\"error\" role=\"alert\">").Append(Encode(text["loginNoMethod"])).Append("</div>");
            sb.Append("</body></html>");
            return ConnectResults.Html(sb.ToString());
        }

        if (showPasswordForm)
            AppendPasswordForm(sb, httpContext, returnUrl);

        if (externalSchemes.Count > 0)
        {
            if (showPasswordForm)
                sb.Append("<div class=\"separator\">").Append(Encode(text["loginOr"])).Append("</div>");

            // Errors land back here, with the same pending authorization to resume.
            var errorUrl = QueryHelpers.AddQueryString("/connect/login", "returnUrl", returnUrl);
            foreach (var scheme in externalSchemes)
            {
                var href = QueryHelpers.AddQueryString("/spark/auth/external-login", new Dictionary<string, string?>
                {
                    ["provider"] = scheme.Name,
                    ["returnUrl"] = returnUrl,
                    ["errorUrl"] = errorUrl,
                });

                sb.Append("<a class=\"btn btn-external\" href=\"").Append(Encode(href)).Append("\">")
                  .Append(Encode(text["loginWithProvider", scheme.DisplayName ?? scheme.Name])).Append("</a>");
            }
        }

        sb.Append("</body></html>");
        return ConnectResults.Html(sb.ToString());
    }

    private void AppendPasswordForm(StringBuilder sb, HttpContext httpContext, string returnUrl)
    {
        sb.Append("<form method=\"post\">");
        ConnectPage.AppendAntiforgery(sb, antiforgery, httpContext);
        sb.Append("<input type=\"hidden\" name=\"returnUrl\" value=\"").Append(Encode(returnUrl)).Append("\" />");
        sb.Append("<div class=\"form-group\">");
        // Labelled from SparkAuthenticationOptions.SignInIdentifiers, which the POST's resolver
        // (SparkSignInManager) enforces; the field name stays "identifier" whatever it accepts.
        var (labelKey, type) = IdentifierField(authenticationOptions.Value.SignInIdentifiers);
        sb.Append("<label for=\"identifier\">").Append(Encode(text[labelKey])).Append("</label>");
        sb.Append("<input type=\"").Append(type).Append("\" id=\"identifier\" name=\"identifier\" autocomplete=\"")
            .Append(type == "email" ? "email" : "username").Append('"');
        // OIDC Core §3.1.2.1: login_hint pre-fills the identifier; a hint, never trusted for anything.
        if (!string.IsNullOrEmpty(LoginHint))
            sb.Append(" value=\"").Append(Encode(LoginHint)).Append('"');
        sb.Append(" required autofocus />");
        sb.Append("</div>");
        sb.Append("<div class=\"form-group\">");
        sb.Append("<label for=\"password\">").Append(Encode(text["loginPassword"])).Append("</label>");
        sb.Append("<input type=\"password\" id=\"password\" name=\"password\" required />");
        sb.Append("</div>");
        sb.Append("<div class=\"form-group\">");
        sb.Append("<label><input type=\"checkbox\" name=\"rememberMe\" value=\"true\" /> ").Append(Encode(text["loginRememberMe"])).Append("</label>");
        sb.Append("</div>");
        sb.Append("<button type=\"submit\" class=\"btn btn-primary\">").Append(Encode(text["loginSubmit"])).Append("</button>");
        sb.Append("</form>");
    }

    /// <summary>The identifier field's label (a text key) and input type.</summary>
    private static (string LabelKey, string Type) IdentifierField(SparkSignInIdentifiers allowed)
    {
        return (allowed.HasFlag(SparkSignInIdentifiers.Email), allowed.HasFlag(SparkSignInIdentifiers.UserName)) switch
        {
            (true, false) => ("loginIdentifierEmail", "email"),
            (false, true) => ("loginIdentifierUserName", "text"),
            _ => ("loginIdentifierEither", "text"),
        };
    }

    private static string Encode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}

/// <summary>The provider's password form as posted.</summary>
internal sealed record OidcLoginSubmission(string? Identifier, string? Password, string? ReturnUrl, bool RememberMe);

/// <summary>Accepts the provider's password form.</summary>
/// <remarks>
/// <para>
/// Generic over the application's user type, closed once when the routes are mapped
/// (<see cref="OidcUserEndpoints"/>), so it signs in through a typed <see cref="SignInManager{TUser}"/>
/// instead of resolving and reflecting on one per request.
/// </para>
/// <para>
/// ⚠️ The antiforgery stamp is explicit: the form is read by <see cref="BindRequestAsync"/>, not by
/// a <c>[FromForm]</c> parameter, so nothing infers the metadata for it — which is how the page once
/// went unprotected.
/// </para>
/// </remarks>
[MemberOf<OidcLocalCredentialsGroup>]
internal sealed partial class OidcLoginSubmit<TUser> : IPostEndpoint<OidcLoginSubmission>
    where TUser : SparkUser, new()
{
    public static string Path => "/login";

    [Inject] private readonly SignInManager<TUser> signInManager;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    /// <summary>
    /// Reads the page's own HTML form. A body that is not a form fails exactly as it did when the
    /// handler read the form itself.
    /// </summary>
    protected override async ValueTask<OidcLoginSubmission?> BindRequestAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcLoginSubmission(
            // "email" is the field name older copies of this page post; "identifier" is the current one.
            Identifier: form["identifier"].FirstOrDefault() ?? form["email"].FirstOrDefault(),
            Password: form["password"].FirstOrDefault(),
            ReturnUrl: form["returnUrl"].FirstOrDefault(),
            RememberMe: string.Equals(form["rememberMe"].FirstOrDefault(), "true", StringComparison.Ordinal));
    }

    public override async Task<IResult> HandleAsync(OidcLoginSubmission submission, CancellationToken ct)
    {
        var identifier = submission.Identifier;
        var password = submission.Password;
        var returnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(submission.ReturnUrl);
        var rememberMe = submission.RememberMe;

        if (string.IsNullOrEmpty(identifier) || string.IsNullOrEmpty(password))
            return RedirectWithError(returnUrl, "missing_fields");

        // Attempt password sign-in through the string overload, which SparkSignInManager overrides:
        // the identifier is an email or a user name, resolved by the same rule as /spark/auth/login
        // (#460, D4). An unknown identifier fails exactly like a wrong password.
        //
        // lockoutOnFailure was false, which meant failures never reached AccessFailedAsync: an
        // unauthenticated, unthrottled endpoint would test passwords forever, and the
        // IsLockedOut branch below was unreachable. MapIdentityApi's own login passes true, so
        // this page was strictly weaker than the API beside it.
        //
        // isPersistent was hardcoded true, silently issuing every visitor a persistent cookie.
        // It now follows the checkbox the user actually saw.
        var result = await signInManager.PasswordSignInAsync(identifier, password, isPersistent: rememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
            return Results.Redirect(returnUrl);

        if (result.RequiresTwoFactor)
        {
            // The remember-me choice is made here and spent on the next hop, so it has to travel.
            var carry = rememberMe ? "&rememberMe=true" : "";
            return Results.Redirect($"/connect/two-factor?returnUrl={Uri.EscapeDataString(returnUrl)}{carry}");
        }

        if (result.IsLockedOut)
            return RedirectWithError(returnUrl, "locked_out");

        return RedirectWithError(returnUrl, "invalid_credentials");
    }

    private static IResult RedirectWithError(string returnUrl, string error)
        => Results.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error={Uri.EscapeDataString(error)}");
}
