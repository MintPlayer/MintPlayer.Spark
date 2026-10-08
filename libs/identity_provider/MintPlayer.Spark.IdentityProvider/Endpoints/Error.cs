using System.Text;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Services;
using static MintPlayer.Spark.IdentityProvider.Endpoints.ConnectPage;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// The provider's error page (<c>GET /connect/error?error=…&amp;error_description=…</c>, PRD D7): where a
/// browser lands when a <c>/connect/*</c> step fails in a way that cannot go back to the client.
/// </summary>
/// <remarks>
/// <para>
/// Both query values are untrusted: anyone can link here with any text. <c>error</c> only selects one of
/// our own messages from an allow-list of OAuth/OIDC error codes (anything else is <c>server_error</c>),
/// for the reason <see cref="ConnectPage.ErrorKey"/> gives. <c>error_description</c> is shown, HTML-encoded
/// and length-capped, only as a muted detail line below our own message, never in the error box.
/// </para>
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcErrorPage : IGetEndpoint<string>
{
    public static string Path => "/error";

    /// <summary>A longer description is cut: it is a detail line, not a place for a page of text.</summary>
    private const int MaxDescriptionLength = 300;

    [QueryParam("error")] public string? Error { get; set; }
    [QueryParam("error_description")] public string? ErrorDescription { get; set; }

    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ConnectText text;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var code = Error is not null && KnownErrors.ContainsKey(Error) ? Error : "server_error";
        var description = string.IsNullOrWhiteSpace(ErrorDescription) ? null
            : ErrorDescription.Length > MaxDescriptionLength ? ErrorDescription[..MaxDescriptionLength] + "…"
            : ErrorDescription;

        var statusCode = code switch
        {
            "server_error" => StatusCodes.Status500InternalServerError,
            "temporarily_unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };

        var html = Render(httpContextAccessor.HttpContext!, text, options.Branding, "errorTitle", KnownErrors[code], code, description);
        return Task.FromResult(ConnectResults.Html(html, statusCode));
    }

    /// <summary>The OAuth 2.0 / OIDC error codes this page names, each with its text key.</summary>
    private static readonly Dictionary<string, string> KnownErrors = new(StringComparer.Ordinal)
    {
        ["invalid_request"] = "errorInvalidRequest",
        ["invalid_client"] = "errorInvalidClient",
        ["invalid_grant"] = "errorInvalidGrant",
        ["unauthorized_client"] = "errorUnauthorizedClient",
        ["unsupported_grant_type"] = "errorUnsupportedGrantType",
        ["unsupported_response_type"] = "errorUnsupportedResponseType",
        ["invalid_scope"] = "errorInvalidScope",
        ["access_denied"] = "errorAccessDenied",
        ["server_error"] = "errorServerError",
        ["temporarily_unavailable"] = "errorTemporarilyUnavailable",
        ["interaction_required"] = "errorInteractionRequired",
        ["login_required"] = "errorLoginRequired",
        ["account_selection_required"] = "errorAccountSelectionRequired",
        ["consent_required"] = "errorConsentRequired",
        ["invalid_request_uri"] = "errorInvalidRequestUri",
        ["invalid_request_object"] = "errorInvalidRequestObject",
        ["request_not_supported"] = "errorRequestNotSupported",
        ["request_uri_not_supported"] = "errorRequestUriNotSupported",
        ["registration_not_supported"] = "errorRegistrationNotSupported",
    };

    /// <summary>
    /// The error page's markup. <paramref name="headingKey"/> and <paramref name="messageKey"/> are text keys
    /// the caller chose; <paramref name="code"/> (allow-listed) and <paramref name="description"/> (untrusted)
    /// are encoded on output.
    /// </summary>
    internal static string Render(
        HttpContext context, ConnectText text, Configuration.SparkIdentityProviderBranding? branding,
        string headingKey, string messageKey, string? code, string? description)
    {
        var heading = text[headingKey];
        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, context, heading, text.Culture, branding);
        sb.Append("body{max-width:480px;margin:80px auto;padding:0 20px}");
        sb.Append(".error{color:var(--idp-error-color);background:var(--idp-error-bg);border:1px solid var(--idp-error-border);padding:8px 12px;border-radius:6px;margin-bottom:16px;font-size:14px}");
        sb.Append(".muted{color:var(--idp-muted);font-size:13px;overflow-wrap:anywhere}");
        sb.Append("</style></head><body>");
        ConnectPageTheme.AppendBrand(sb, branding);

        sb.Append("<h2>").Append(Encode(heading)).Append("</h2>");
        sb.Append("<div class=\"error\" role=\"alert\">").Append(Encode(text[messageKey])).Append("</div>");
        if (!string.IsNullOrEmpty(description))
            sb.Append("<p class=\"muted\">").Append(Encode(text["errorDetails", description])).Append("</p>");
        if (!string.IsNullOrEmpty(code))
            sb.Append("<p class=\"muted\">").Append(Encode(text["errorCode", code])).Append("</p>");

        sb.Append("</body></html>");
        return sb.ToString();
    }
}
