using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace MintPlayer.Spark.Authorization.Pages;

/// <summary>
/// The branding a server-rendered page shows (#490 D11): the identity provider's
/// <c>Spark:IdentityProvider:Branding</c> implements it.
/// </summary>
public interface ISparkPageBranding
{
    string? ProductName { get; }
    string? LogoUrl { get; }
    string? ExtraCss { get; }
}

/// <summary>HTML helpers shared by Spark's server-rendered pages (<see cref="ConnectPageTheme"/>).</summary>
internal static class SparkPageHtml
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
    public static void AppendAntiforgery(StringBuilder sb, IAntiforgery antiforgery, HttpContext context)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        AppendHidden(sb, tokens.FormFieldName, tokens.RequestToken);
    }
}
