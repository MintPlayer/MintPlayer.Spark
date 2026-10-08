using System.Text.RegularExpressions;
using ExternalLoginErrors = MintPlayer.Spark.Authorization.Extensions.SparkAuthenticationExtensions.ExternalLoginErrors;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// The external-login hand-off nonce (#490, D1): the value a popup flow's opener generates and the
/// callback page echoes back, so the opener can tell its own result apart on the shared
/// <c>BroadcastChannel</c> and <c>localStorage</c> channels.
/// </summary>
/// <remarks>
/// ⚠️ The nonce is caller data that ends up inside a <c>&lt;script&gt;</c> on the callback page. It is
/// therefore held to a strict shape <b>and</b> JSON-encoded there; either alone would be enough today,
/// both together mean a later change to one cannot reopen the hole.
/// </remarks>
internal static partial class SparkExternalLoginNonce
{
    /// <summary>The query-string parameter carrying the nonce on the challenge and callback URLs.</summary>
    public const string QueryParameter = "nonce";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{16,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    /// <summary>Whether <paramref name="nonce"/> has the accepted shape (16–64 base64url characters).</summary>
    public static bool IsValid(string nonce) => Shape().IsMatch(nonce);

    /// <summary>
    /// Challenge side: <see langword="null"/> when the nonce is absent (the pre-#490 flow) or valid,
    /// otherwise the 400 <see cref="ExternalLoginErrors.InvalidNonce"/> to answer with.
    /// </summary>
    public static IResult? Reject(string? nonce)
        => string.IsNullOrEmpty(nonce) || IsValid(nonce)
            ? null
            : Results.BadRequest(new { error = ExternalLoginErrors.InvalidNonce });

    /// <summary>
    /// Callback side: the nonce when it has the accepted shape, otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The callback is anonymous and its URL can be typed by anyone, so the challenge's check does not
    /// protect it. A malformed nonce there is dropped rather than refused: by the time the callback
    /// reports, the sign-in has already happened, and the page still has to tell <em>someone</em>.
    /// </remarks>
    public static string? Accept(string? nonce)
        => !string.IsNullOrEmpty(nonce) && IsValid(nonce) ? nonce : null;

    /// <summary>
    /// Appends the hand-off flags to a callback URL that already has a query string: <c>popup=1</c>
    /// when asked for, the (already validated) nonce when present, and always <c>ngsw-bypass=true</c>
    /// so an Angular service worker never answers the callback navigation from its cache.
    /// </summary>
    /// <remarks>
    /// The callback is a fresh top-level navigation, so the only thing that survives the provider hop
    /// is this URL — which ASP.NET encrypts into OAuth <c>state</c> as
    /// <c>AuthenticationProperties.RedirectUri</c>. The provider never sees it.
    /// </remarks>
    public static string AppendCallbackFlags(string callbackUrl, string? popup, string? nonce)
    {
        if (!string.IsNullOrEmpty(popup))
            callbackUrl += "&popup=1";
        if (!string.IsNullOrEmpty(nonce))
            callbackUrl += $"&{QueryParameter}={Uri.EscapeDataString(nonce)}";
        return callbackUrl + "&ngsw-bypass=true";
    }
}
