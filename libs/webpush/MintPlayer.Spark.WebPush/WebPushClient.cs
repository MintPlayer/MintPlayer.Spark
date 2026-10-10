using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.WebPush;

/// <summary>What the push service said, mapped to what the sender should do next.</summary>
public enum WebPushOutcome
{
    /// <summary>201 (or another 2xx): accepted for delivery.</summary>
    Delivered,

    /// <summary>404 or 410: the subscription expired or was unsubscribed. Delete it.</summary>
    SubscriptionGone,

    /// <summary>429 or 5xx: try again later, after <see cref="WebPushResult.RetryAfter"/> when given.</summary>
    RetryLater,

    /// <summary>Any other status (400 and 403 are a VAPID/configuration bug, 413 a payload too large). Retrying will not help.</summary>
    Rejected,
}

/// <summary>The push service's answer.</summary>
/// <param name="Outcome">What to do next.</param>
/// <param name="StatusCode">The HTTP status.</param>
/// <param name="RetryAfter">The <c>Retry-After</c> of a <see cref="WebPushOutcome.RetryLater"/>, when sent.</param>
/// <param name="Reason">The start of the response body (Apple and Mozilla explain refusals there), at most 512 characters.</param>
public sealed record WebPushResult(WebPushOutcome Outcome, HttpStatusCode StatusCode, TimeSpan? RetryAfter = null, string? Reason = null);

/// <summary>
/// Sends RFC 8030 push messages: the RFC 8291 body, the RFC 8292 <c>Authorization</c> header, <c>TTL</c>,
/// <c>Urgency</c> and <c>Topic</c>. Every HTTP outcome is a <see cref="WebPushResult"/>; only transport
/// failures (DNS, TLS, a refusal by an SSRF guard) throw.
/// </summary>
/// <remarks>
/// The push endpoint comes from a browser, so hand this client an SSRF-safe <see cref="HttpClient"/>
/// (in Spark: the <c>AddSafeHttp()</c> client). The client is thread-safe; keep one per VAPID identity.
/// </remarks>
public sealed partial class WebPushClient(HttpClient httpClient, VapidAuthorization vapid)
{
    /// <summary>Encrypts and sends <paramref name="message"/> to <paramref name="subscription"/>.</summary>
    /// <exception cref="ArgumentException">The subscription is malformed (not https, bad keys), or the message breaks a limit.</exception>
    public async Task<WebPushResult> SendAsync(WebPushSubscription subscription, WebPushMessage message, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A push endpoint is an absolute https URL.", nameof(subscription));
        if (message.TimeToLive < TimeSpan.Zero)
            throw new ArgumentException("The TTL cannot be negative.", nameof(message));
        if (message.Topic is { } topic && !TopicPattern().IsMatch(topic))
            throw new ArgumentException("A topic is 1–32 base64url characters (RFC 8030 §5.4).", nameof(message));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", vapid.HeaderFor(endpoint));
        request.Headers.TryAddWithoutValidation("TTL", ((long)message.TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (message.Urgency is { } urgency)
            request.Headers.TryAddWithoutValidation("Urgency", UrgencyText(urgency));
        if (message.Topic is not null)
            request.Headers.TryAddWithoutValidation("Topic", message.Topic);

        var body = message.Payload.IsEmpty
            ? Array.Empty<byte>()
            : WebPushEncryption.Encrypt(message.Payload.Span, subscription);
        request.Content = new ByteArrayContent(body);
        if (body.Length > 0)
        {
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        return await ResultAsync(response, cancellationToken);
    }

    internal static async Task<WebPushResult> ResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        var code = (int)status;
        if (code is >= 200 and < 300)
            return new WebPushResult(WebPushOutcome.Delivered, status);

        string? reason = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            reason = text.Length > 512 ? text[..512] : text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The status decides; the body is only an explanation.
        }

        if (code is 404 or 410)
            return new WebPushResult(WebPushOutcome.SubscriptionGone, status, Reason: reason);
        if (code == 429 || code >= 500)
            return new WebPushResult(WebPushOutcome.RetryLater, status, RetryAfter(response), reason);
        return new WebPushResult(WebPushOutcome.Rejected, status, Reason: reason);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retry)
            return null;
        if (retry.Delta is { } delta)
            return delta;
        if (retry.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static string UrgencyText(WebPushUrgency urgency) => urgency switch
    {
        WebPushUrgency.VeryLow => "very-low",
        WebPushUrgency.Low => "low",
        WebPushUrgency.High => "high",
        _ => "normal",
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex TopicPattern();
}
