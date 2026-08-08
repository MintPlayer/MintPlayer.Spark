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
