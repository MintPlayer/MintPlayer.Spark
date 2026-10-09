using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using MintPlayer.Spark.IdentityProvider.Services;

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
    public static string Encode(string value) => SparkPageHtml.Encode(value);

    /// <summary>
    /// The text key (<c>identityProvider.connect.*</c>, resolved through <c>ConnectText</c>) of the message
    /// for an error code carried in the query string, or null if there is none.
    /// <para>
    /// The pages used to render the query value itself. It was HTML-encoded, so there was no
    /// XSS — but it let anyone put their own words inside the identity provider's own styled
    /// error box, on its real origin, above a real password field. "Your session expired,
    /// confirm your password" is a convincing thing to read there. Codes map to fixed strings
    /// and anything unrecognised falls back to a generic one, so the attacker's only remaining
    /// choice is *which* of our messages to show.
    /// </para>
    /// </summary>
    public static string? ErrorKey(string? code) => code switch
    {
        null or "" => null,
        "missing_fields" => "signInErrorMissingFields",
        // The field takes an email or a user name (D4), so the message names both.
        "invalid_credentials" => "signInErrorInvalidCredentials",
        "locked_out" => "signInErrorLockedOut",
        "missing_code" => "signInErrorMissingCode",
        "missing_recovery_code" => "signInErrorMissingRecoveryCode",
        "invalid_code" => "signInErrorInvalidCode",
        "invalid_recovery_code" => "signInErrorInvalidRecoveryCode",
        _ => "signInErrorFailed",
    };

    /// <summary>
    /// The text key of the message for a <c>?sparkExternalLogin=&lt;code&gt;</c> outcome (#490 M6), or null if
    /// there is none.
    /// </summary>
    /// <remarks>
    /// Fixed texts, for the reason <see cref="ErrorKey"/> gives; their <c>en</c> values match the
    /// Authorization package's <c>auth.externalLoginError.*</c> translations, which the Angular sign-in page
    /// shows for the same codes. An unknown code gets a generic message.
    /// </remarks>
    public static string? ExternalLoginKey(string? code) => code switch
    {
        null or "" => null,
        "no_login_info" => "externalLoginNoLoginInfo",
        "email_not_verified" => "externalLoginEmailNotVerified",
        "account_creation_failed" => "externalLoginAccountCreationFailed",
        "email_already_registered" => "externalLoginEmailAlreadyRegistered",
        "sign_in_to_link" => "externalLoginSignInToLink",
        "link_confirmation_sent" => "externalLoginLinkConfirmationSent",
        "confirm_email_sent" => "externalLoginConfirmEmailSent",
        "remote_failure" => "externalLoginRemoteFailure",
        "invalid_nonce" => "externalLoginInvalidNonce",
        _ => "externalLoginFailed",
    };

    /// <summary>Whether an external-login code is a "check your mail" notice rather than a failure.</summary>
    public static bool IsExternalLoginNotice(string? code)
        => code is "link_confirmation_sent" or "confirm_email_sent";

    public static void AppendHidden(StringBuilder sb, string name, string? value) => SparkPageHtml.AppendHidden(sb, name, value);

    /// <summary>Writes the antiforgery field (<see cref="SparkPageHtml.AppendAntiforgery"/>).</summary>
    public static void AppendAntiforgery(StringBuilder sb, IAntiforgery antiforgery, HttpContext context) => SparkPageHtml.AppendAntiforgery(sb, antiforgery, context);
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

    /// <summary>A rendered page: 200 unless named, <c>text/html; charset=utf-8</c>.</summary>
    public static IResult Html(string body, int statusCode = StatusCodes.Status200OK)
        => new TextResult(statusCode, "text/html; charset=utf-8", body);

    /// <summary>
    /// A refusal a person sees in the browser, rendered as the localized, branded error page with
    /// <paramref name="statusCode"/> (D7). <paramref name="messageKey"/> and <paramref name="headingKey"/> are
    /// <c>identityProvider.connect.*</c> text keys chosen by the caller, never request input.
    /// </summary>
    public static IResult ErrorPage(
        HttpContext context, ConnectText text, Configuration.SparkIdentityProviderBranding? branding,
        int statusCode, string messageKey, string? headingKey = null)
        => Html(OidcErrorPage.Render(context, text, branding, headingKey ?? "errorTitle", messageKey, code: null, description: null), statusCode);

    /// <summary>
    /// Sends the browser to <c>/connect/error</c>, for a caller that cannot render the page itself. The page
    /// shows only an allow-listed <paramref name="error"/> and encodes <paramref name="description"/>.
    /// </summary>
    public static IResult ErrorRedirect(string error, string? description = null)
    {
        var query = new Dictionary<string, string?> { ["error"] = error };
        if (!string.IsNullOrEmpty(description)) query["error_description"] = description;
        return Results.Redirect(QueryHelpers.AddQueryString("/connect/error", query));
    }

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
