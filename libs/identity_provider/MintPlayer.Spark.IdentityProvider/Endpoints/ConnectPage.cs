using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Shared markup helpers for the three interactive <c>/connect</c> pages, which render inline
/// HTML rather than going through a view engine.
/// <para>
/// <see cref="AppendAntiforgery"/> lives here rather than in each page because a missing CSRF
/// token is invisible: the form keeps working, and only the protection disappears. Sharing it
/// is what makes "every rendered form is protected" checkable in one place.
/// </para>
/// </summary>
internal static class ConnectPage
{
    public static string Encode(string value) => System.Net.WebUtility.HtmlEncode(value);

    /// <summary>
    /// The message for an error code carried in the query string, or null if there is none.
    /// <para>
    /// The pages used to render the query value itself. It was HTML-encoded, so there was no
    /// XSS — but it let anyone put their own words inside the identity provider's own styled
    /// error box, on its real origin, above a real password field. "Your session expired,
    /// confirm your password" is a convincing thing to read there. Codes map to fixed strings
    /// and anything unrecognised falls back to a generic one, so the attacker's only remaining
    /// choice is *which* of our messages to show.
    /// </para>
    /// </summary>
    public static string? ErrorMessage(string? code) => code switch
    {
        null or "" => null,
        "missing_fields" => "Email or user name, and password, are required.",
        // The field takes an email or a user name (D4), so the message names both.
        "invalid_credentials" => "Invalid email/user name or password.",
        "locked_out" => "Account is locked out. Please try again later.",
        "missing_code" => "Please enter your authentication code.",
        "missing_recovery_code" => "Please enter a recovery code.",
        "invalid_code" => "Invalid authentication code.",
        "invalid_recovery_code" => "Invalid recovery code.",
        _ => "Sign-in failed. Please try again.",
    };

    /// <summary>
    /// The message for a <c>?sparkExternalLogin=&lt;code&gt;</c> outcome (#490 M6), or null if there is none.
    /// </summary>
    /// <remarks>
    /// Fixed English strings, like <see cref="ErrorMessage"/> and the rest of these pages; the texts match
    /// the <c>en</c> values of the Authorization package's <c>auth.externalLoginError.*</c> translations,
    /// which the Angular sign-in page shows for the same codes. An unknown code gets a generic message.
    /// </remarks>
    public static string? ExternalLoginMessage(string? code) => code switch
    {
        null or "" => null,
        "no_login_info" => "The provider did not return a login.",
        "email_not_verified" => "The provider has not verified that email address.",
        "account_creation_failed" => "The account could not be created. Please try again.",
        "email_already_registered" => "An account with that email address already exists. Sign in to it first, then connect this login from your account page.",
        "sign_in_to_link" => "Sign in first to connect this login to your account.",
        "link_confirmation_sent" => "We sent you an email. Follow its link to connect this login to your account.",
        "confirm_email_sent" => "We sent you an email. Confirm your address, then sign in again.",
        "remote_failure" => "The provider reported a problem with the sign-in. Please try again.",
        "invalid_nonce" => "The sign-in request was not valid. Please try again.",
        _ => "Sign-in with the external provider failed. Please try again.",
    };

    /// <summary>Whether an external-login code is a "check your mail" notice rather than a failure.</summary>
    public static bool IsExternalLoginNotice(string? code)
        => code is "link_confirmation_sent" or "confirm_email_sent";

    public static void AppendHidden(StringBuilder sb, string name, string? value)
    {
        sb.Append("<input type=\"hidden\" name=\"").Append(Encode(name))
          .Append("\" value=\"").Append(Encode(value ?? "")).Append("\" />");
    }

    /// <summary>
    /// Writes the antiforgery field for a form whose POST route is marked with
    /// <c>RequireAntiforgeryTokenAttribute</c>. Must be called inside the <c>&lt;form&gt;</c>.
    /// The page passes the <see cref="IAntiforgery"/> it injected.
    /// </summary>
    public static void AppendAntiforgery(StringBuilder sb, IAntiforgery antiforgery, HttpContext context)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        AppendHidden(sb, tokens.FormFieldName, tokens.RequestToken);
    }
}

/// <summary>
/// The text responses of the <c>/connect</c> pages, written exactly as the handlers wrote them before
/// they became endpoint classes (M4).
/// </summary>
/// <remarks>
/// Not <c>Results.Text</c>/<c>Results.Content</c>: those default the content type to
/// <c>text/plain; charset=utf-8</c> and set <c>Content-Length</c>, and the bare refusals here never
/// carried a content type. Writing through <c>WriteAsync</c> keeps every header and byte as it was
/// (pinned by <c>OidcResponseShapeTests</c>).
/// </remarks>
internal static class ConnectResults
{
    /// <summary>A status and a text body, with no content type unless one is named.</summary>
    public static IResult Text(int statusCode, string body, string? contentType = null)
        => new TextResult(statusCode, contentType, body);

    /// <summary>A rendered page: 200, <c>text/html; charset=utf-8</c>.</summary>
    public static IResult Html(string body)
        => new TextResult(StatusCodes.Status200OK, "text/html; charset=utf-8", body);

    private sealed class TextResult(int statusCode, string? contentType, string body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            if (contentType is not null)
                httpContext.Response.ContentType = contentType;
            return httpContext.Response.WriteAsync(body, httpContext.RequestAborted);
        }
    }
}
