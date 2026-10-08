using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using ExternalLoginErrors = MintPlayer.Spark.Authorization.Extensions.SparkAuthenticationExtensions.ExternalLoginErrors;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// The default <c>OnRemoteFailure</c> of every Spark external-provider preset (#490, D4): reports a
/// failure at the provider hop the same way the callback reports any other outcome.
/// </summary>
/// <remarks>
/// <para>
/// Without it, a user who cancels at the provider — or a round trip whose correlation fails — got
/// ASP.NET's unhandled-exception page inside the popup, and the opener never heard anything.
/// </para>
/// <para>
/// The failed request is the provider's redirect to the handler's <c>CallbackPath</c>, not Spark's
/// callback, so the popup flag, nonce and return URL are read from the round trip's
/// <c>AuthenticationProperties.RedirectUri</c> — the callback URL the challenge built. When that is
/// missing (the state could not be read at all), there is nothing to report to but the site root.
/// </para>
/// <para>
/// Assigned before the application's configure callback runs, so an application can still replace it.
/// </para>
/// </remarks>
internal static class SparkExternalLoginRemoteFailure
{
    /// <summary>The OAuth error a provider returns when the user declines (RFC 6749 §4.1.2.1).</summary>
    private const string AccessDenied = "access_denied";

    public static async Task Handle(RemoteFailureContext context)
    {
        var code = IsAccessDenied(context) ? ExternalLoginErrors.NoLoginInfo : ExternalLoginErrors.RemoteFailure;

        var redirectUri = context.Properties?.RedirectUri;
        IResult outcome;
        if (string.IsNullOrEmpty(redirectUri))
        {
            outcome = SparkAuthenticationExtensions.ExternalLoginOutcome(popup: false, nonce: null, "/", code);
        }
        else
        {
            var queryStart = redirectUri.IndexOf('?');
            var query = queryStart < 0
                ? new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>()
                : QueryHelpers.ParseQuery(redirectUri[queryStart..]);

            query.TryGetValue("returnUrl", out var returnUrl);
            query.TryGetValue(SparkExternalLoginNonce.QueryParameter, out var nonce);
            // #490 M6: a redirect-mode failure lands on the page that asked to show it.
            query.TryGetValue(SparkAuthenticationExtensions.ErrorUrlParameter, out var errorUrl);

            outcome = SparkAuthenticationExtensions.ExternalLoginOutcome(
                popup: query.ContainsKey("popup"),
                nonce: SparkExternalLoginNonce.Accept(nonce),
                safeReturnUrl: SparkAuthenticationExtensions.SanitizeReturnUrl(returnUrl),
                error: code,
                safeErrorUrl: SparkAuthenticationExtensions.SanitizeErrorUrl(errorUrl));
        }

        await outcome.ExecuteAsync(context.HttpContext);
        context.HandleResponse();
    }

    /// <summary>
    /// Whether the provider reported that the user declined.
    /// </summary>
    /// <remarks>
    /// ASP.NET's remote handlers (OAuth and Twitter alike) raise that case as a failure whose message
    /// says access was denied. The provider's own <c>error=access_denied</c> on the request is the
    /// fallback for handlers that word it differently — but only when the round trip itself held up:
    /// a correlation or state failure is <see cref="ExternalLoginErrors.RemoteFailure"/> whatever the
    /// query says, because then nobody can vouch that this response belongs to this browser's flow.
    /// </remarks>
    private static bool IsAccessDenied(RemoteFailureContext context)
    {
        var message = context.Failure?.Message ?? string.Empty;
        if (message.Contains("Access was denied", StringComparison.OrdinalIgnoreCase))
            return true;

        var roundTripFailed = message.Contains("Correlation failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("state was missing or invalid", StringComparison.OrdinalIgnoreCase);

        return !roundTripFailed
            && string.Equals(context.Request.Query["error"], AccessDenied, StringComparison.Ordinal);
    }
}
