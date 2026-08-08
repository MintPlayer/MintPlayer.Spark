using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
        "missing_fields" => "Email and password are required.",
        "invalid_credentials" => "Invalid email or password.",
        "locked_out" => "Account is locked out. Please try again later.",
        "missing_code" => "Please enter your authentication code.",
        "missing_recovery_code" => "Please enter a recovery code.",
        "invalid_code" => "Invalid authentication code.",
        "invalid_recovery_code" => "Invalid recovery code.",
        _ => "Sign-in failed. Please try again.",
    };

    public static void AppendHidden(StringBuilder sb, string name, string? value)
    {
        sb.Append("<input type=\"hidden\" name=\"").Append(Encode(name))
          .Append("\" value=\"").Append(Encode(value ?? "")).Append("\" />");
    }

    /// <summary>
    /// Writes the antiforgery field for a form whose POST route is marked with
    /// <c>RequireAntiforgeryTokenAttribute</c>. Must be called inside the <c>&lt;form&gt;</c>.
    /// </summary>
    public static void AppendAntiforgery(StringBuilder sb, HttpContext context)
    {
        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        var tokens = antiforgery.GetAndStoreTokens(context);
        AppendHidden(sb, tokens.FormFieldName, tokens.RequestToken);
    }
}
