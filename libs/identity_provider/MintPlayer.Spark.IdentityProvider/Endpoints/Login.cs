using MintPlayer.Spark.Authorization.Extensions;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

// The login page for the OIDC authorization flow: GET renders an HTML login form, POST authenticates
// and redirects to returnUrl. Membership of OidcLocalCredentialsGroup is what gates both on
// SparkLocalCredentials - see that group's IsEnabled.

/// <summary>Renders the provider's password form.</summary>
[MemberOf<OidcLocalCredentialsGroup>]
internal sealed partial class OidcLoginPage : IGetEndpoint<string>
{
    public static string Path => "/login";

    [QueryParam("returnUrl")] public string? ReturnUrl { get; set; }
    [QueryParam("error")] public string? Error { get; set; }

    [Inject] private readonly IAntiforgery antiforgery;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> authenticationOptions;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var httpContext = httpContextAccessor.HttpContext!;

        // Sanitized at the point of read: an unvalidated returnUrl sends a freshly-authenticated
        // user off-origin, which is high-value phishing precisely because they really did just
        // authenticate here. Shared with the Authorization package rather than duplicated.
        var returnUrl = SparkAuthenticationExtensions.SanitizeReturnUrl(ReturnUrl);
        var error = Error;

        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, httpContext, "Login");
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
        sb.Append("</style></head><body>");
        sb.Append("<h2>Login</h2>");

        if (!string.IsNullOrEmpty(error))
        {
            sb.Append("<div class=\"error\">").Append(Encode(ConnectPage.ErrorMessage(error)!)).Append("</div>");
        }

        sb.Append("<form method=\"post\">");
        ConnectPage.AppendAntiforgery(sb, antiforgery, httpContext);
        sb.Append("<input type=\"hidden\" name=\"returnUrl\" value=\"").Append(Encode(returnUrl)).Append("\" />");
        sb.Append("<div class=\"form-group\">");
        // Labelled from SparkAuthenticationOptions.SignInIdentifiers, which the POST's resolver
        // (SparkSignInManager) enforces; the field name stays "identifier" whatever it accepts.
        var (label, type) = IdentifierField(authenticationOptions.Value.SignInIdentifiers);
        sb.Append("<label for=\"identifier\">").Append(label).Append("</label>");
        sb.Append("<input type=\"").Append(type).Append("\" id=\"identifier\" name=\"identifier\" autocomplete=\"")
            .Append(type == "email" ? "email" : "username").Append("\" required autofocus />");
        sb.Append("</div>");
        sb.Append("<div class=\"form-group\">");
        sb.Append("<label for=\"password\">Password</label>");
        sb.Append("<input type=\"password\" id=\"password\" name=\"password\" required />");
        sb.Append("</div>");
        sb.Append("<div class=\"form-group\">");
        sb.Append("<label><input type=\"checkbox\" name=\"rememberMe\" value=\"true\" /> Remember me</label>");
        sb.Append("</div>");
        sb.Append("<button type=\"submit\" class=\"btn btn-primary\">Login</button>");
        sb.Append("</form></body></html>");

        return Task.FromResult(ConnectResults.Html(sb.ToString()));
    }

    private static (string Label, string Type) IdentifierField(SparkSignInIdentifiers allowed)
    {
        return (allowed.HasFlag(SparkSignInIdentifiers.Email), allowed.HasFlag(SparkSignInIdentifiers.UserName)) switch
        {
            (true, false) => ("Email", "email"),
            (false, true) => ("User name", "text"),
            _ => ("Email or user name", "text"),
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
