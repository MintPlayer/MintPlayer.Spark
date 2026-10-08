using System.Text;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.IdentityProvider.Endpoints;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Delivers an authorization response to the client's redirect URI (<c>docs/identity_provider_platform_PRD.md</c> D8):
/// as query parameters (the default for <c>code</c>) or as an auto-submitting form
/// (<c>response_mode=form_post</c>, OAuth 2.0 Form Post Response Mode), always carrying <c>iss</c>
/// (RFC 9207), so a client talking to several providers can tell which one answered.
/// </summary>
internal static class OidcAuthorizationResponse
{
    public const string FormPost = "form_post";
    public const string Query = "query";

    /// <summary>The response modes this provider supports, for discovery.</summary>
    public static readonly string[] Supported = [Query, FormPost];

    public static IResult Deliver(string redirectUri, string? responseMode, string issuer, params (string Name, string? Value)[] parameters)
    {
        var all = parameters.Append((Name: "iss", Value: (string?)issuer)).Where(p => p.Value is not null).ToArray();
        if (responseMode != FormPost)
            return Results.Redirect(RedirectUrl.With(redirectUri, all));

        // The browser posts the values to the client; nothing lands in its history or a Referer.
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Submit</title></head>")
          .Append("<body onload=\"document.forms[0].submit()\"><form method=\"post\" action=\"")
          .Append(ConnectPage.Encode(redirectUri)).Append("\">");
        foreach (var (name, value) in all)
            ConnectPage.AppendHidden(sb, name, value);
        sb.Append("<noscript><button type=\"submit\">Continue</button></noscript></form></body></html>");
        // The page's own inline handler must run: a page-wide CSP without 'unsafe-inline' would block it.
        return ConnectResults.Html(sb.ToString());
    }

    public static IResult Error(string redirectUri, string? responseMode, string issuer, string? state, string error, string description)
        => Deliver(redirectUri, responseMode, issuer, ("error", error), ("error_description", description), ("state", state));
}
