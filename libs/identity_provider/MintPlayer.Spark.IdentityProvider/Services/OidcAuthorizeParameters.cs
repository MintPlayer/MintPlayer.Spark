using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// An authorization request's parameters (OpenID Connect Core §3.1.2.1, RFC 6749 §4.1.1, RFC 7636,
/// RFC 8707, RFC 9126, RFC 9101), whichever way they arrived: the query, a POSTed form, a pushed
/// request (PAR) or a signed request object (JAR). Everything after parsing works on this, never on
/// the raw request (<c>docs/identity_provider_platform_PRD.md</c> D8).
/// </summary>
public sealed record OidcAuthorizeParameters
{
    public string? ClientId { get; init; }
    public string? RedirectUri { get; init; }
    public string? ResponseType { get; init; }
    public string? ResponseMode { get; init; }
    public string? Scope { get; init; }
    public string? State { get; init; }
    public string? Nonce { get; init; }
    public string? CodeChallenge { get; init; }
    public string? CodeChallengeMethod { get; init; }
    /// <summary><c>none</c>, <c>login</c>, <c>consent</c>, <c>select_account</c>, space-separated.</summary>
    public string? Prompt { get; init; }
    public string? MaxAge { get; init; }
    public string? LoginHint { get; init; }
    public string? UiLocales { get; init; }
    public string? AcrValues { get; init; }
    /// <summary>The <c>claims</c> request parameter (OIDC Core §5.5), JSON.</summary>
    public string? Claims { get; init; }
    public string? IdTokenHint { get; init; }
    /// <summary>RFC 8707 resource indicators; may repeat.</summary>
    public IReadOnlyList<string> Resources { get; init; } = [];
    public string? IncludeGrantedScopes { get; init; }
    /// <summary>A signed request object (JAR, RFC 9101), passed by value.</summary>
    public string? Request { get; init; }
    /// <summary>A reference to a pushed request (PAR, RFC 9126) or a request object (JAR).</summary>
    public string? RequestUri { get; init; }
    /// <summary>Carried by the sign-in page when it bounced a refused external sign-in back (#490 M6).</summary>
    public string? ExternalLogin { get; init; }

    public IReadOnlyList<string> Prompts => (Prompt ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public int? MaxAgeSeconds => int.TryParse(MaxAge, out var seconds) && seconds >= 0 ? seconds : null;

    /// <summary>Reads the parameters from a query string or a form.</summary>
    public static OidcAuthorizeParameters From(Func<string, StringValues> read) => new()
    {
        ClientId = First(read("client_id")),
        RedirectUri = First(read("redirect_uri")),
        ResponseType = First(read("response_type")),
        ResponseMode = First(read("response_mode")),
        Scope = First(read("scope")),
        State = First(read("state")),
        Nonce = First(read("nonce")),
        CodeChallenge = First(read("code_challenge")),
        CodeChallengeMethod = First(read("code_challenge_method")),
        Prompt = First(read("prompt")),
        MaxAge = First(read("max_age")),
        LoginHint = First(read("login_hint")),
        UiLocales = First(read("ui_locales")),
        AcrValues = First(read("acr_values")),
        Claims = First(read("claims")),
        IdTokenHint = First(read("id_token_hint")),
        Resources = [.. read("resource").Where(r => !string.IsNullOrEmpty(r)).Select(r => r!)],
        IncludeGrantedScopes = First(read("include_granted_scopes")),
        Request = First(read("request")),
        RequestUri = First(read("request_uri")),
        ExternalLogin = First(read("sparkExternalLogin")),
    };

    public static OidcAuthorizeParameters FromQuery(IQueryCollection query) => From(name => query[name]);

    public static OidcAuthorizeParameters FromForm(IFormCollection form) => From(name => form[name]);

    /// <summary>
    /// The parameters as a query string, for coming back to <c>/connect/authorize</c> after sign-in. A
    /// request that came from PAR travels as its <c>request_uri</c> only (RFC 9126 §4), so nothing the
    /// client pushed is exposed in the browser.
    /// </summary>
    public string ToQueryString(params (string Name, string? Value)[] extra)
    {
        IEnumerable<(string Name, string? Value)> pairs = RequestUri is not null && Request is null && ClientId is not null && IsPushed
            ? [("client_id", ClientId), ("request_uri", RequestUri)]
            :
            [
                ("client_id", ClientId), ("redirect_uri", RedirectUri), ("response_type", ResponseType),
                ("response_mode", ResponseMode), ("scope", Scope), ("state", State), ("nonce", Nonce),
                ("code_challenge", CodeChallenge), ("code_challenge_method", CodeChallengeMethod),
                ("prompt", Prompt), ("max_age", MaxAge), ("login_hint", LoginHint), ("ui_locales", UiLocales),
                ("acr_values", AcrValues), ("claims", Claims), ("id_token_hint", IdTokenHint),
                .. Resources.Select(r => ("resource", (string?)r)),
                ("include_granted_scopes", IncludeGrantedScopes), ("request", Request), ("request_uri", RequestUri),
            ];
        return QueryString.Create(pairs.Concat(extra)
            .Where(p => p.Value is not null)
            .Select(p => KeyValuePair.Create(p.Name, p.Value))).Value ?? "";
    }

    /// <summary>Whether <see cref="RequestUri"/> names a pushed request of this provider (PAR).</summary>
    public bool IsPushed => RequestUri?.StartsWith(PushedRequestUriPrefix, StringComparison.Ordinal) == true;

    /// <summary>RFC 9126 §2.2: the <c>request_uri</c> a pushed request is referenced by.</summary>
    public const string PushedRequestUriPrefix = "urn:ietf:params:oauth:request_uri:";

    private static string? First(StringValues values) => values.Count > 0 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;
}
