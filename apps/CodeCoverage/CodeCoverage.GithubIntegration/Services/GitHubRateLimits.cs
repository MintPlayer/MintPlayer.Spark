using System.Globalization;
using System.Net;
using CodeCoverage.Forge;

namespace CodeCoverage.Services;

/// <summary>
/// Tells GitHub's rate limits apart from its refusals (dependency-updates PRD P16, R37), from the real captures in
/// <c>CodeCoverage.Tests/Fixtures/GitHub/RateLimits</c>:
/// <list type="bullet">
/// <item><description><b>Primary:</b> 403 (or 429) with <c>x-ratelimit-remaining: 0</c> and no <c>retry-after</c>, on
/// <c>git/trees</c> too, so a throttled read is never a 404. Waits until <c>x-ratelimit-reset</c>.</description></item>
/// <item><description><b>Secondary:</b> 403 or 429 whose message says "secondary rate limit" (or any "rate limit"), with
/// <c>x-ratelimit-remaining</c> above zero; honours <c>retry-after</c>, otherwise waits at least a minute.</description></item>
/// <item><description><b>429</b> with nothing else to go on: a minute.</description></item>
/// <item><description><b>A permission 403</b> ("Must have push access…", "You must be an org admin…"): remaining above
/// zero, no <c>retry-after</c>, no "rate limit" in the message. Not a rate limit: null, and the caller refuses.</description></item>
/// <item><description><b>GraphQL:</b> HTTP 200 with <c>errors[].type == "RATE_LIMITED"</c>.</description></item>
/// </list>
/// </summary>
public static class GitHubRateLimits
{
    /// <summary>GitHub's documented floor for a secondary limit without <c>retry-after</c>: "wait at least one minute".</summary>
    public static readonly TimeSpan SecondaryMinimum = TimeSpan.FromMinutes(1);

    /// <summary>Added to a primary limit's reset: GitHub's clock and ours differ by a little, and one second early is a second 403.</summary>
    public static readonly TimeSpan ResetSkew = TimeSpan.FromSeconds(5);

    /// <summary>The rate limit an HTTP answer is, or null when it is not one.</summary>
    /// <param name="header">Reads one response header by name (case-insensitive), or null.</param>
    /// <param name="what">The request, for the message (<c>GET repos/acme/widget/git/trees/…</c>).</param>
    public static ForgeRateLimitedException? Classify(HttpStatusCode status, Func<string, string?> header, string? message, DateTimeOffset now, string what)
    {
        if (status is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
            return null;

        var retryAfter = RetryAfter(header("retry-after"), now);
        var remaining = header("x-ratelimit-remaining");
        var reset = long.TryParse(header("x-ratelimit-reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : (DateTimeOffset?)null;
        var saysRateLimit = message is not null && message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        var saysSecondary = message is not null && message.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase);

        if (remaining == "0" && !saysSecondary && retryAfter is null)
        {
            var wait = reset is { } at ? Max(at - now + ResetSkew, TimeSpan.Zero) : SecondaryMinimum;
            return new ForgeRateLimitedException(wait, reset, EForgeRateLimitKind.Primary,
                $"GitHub's rate limit is spent on {what}{(reset is { } r ? $"; it resets at {r:HH:mm:ss} UTC" : string.Empty)}.");
        }
        if (saysRateLimit || retryAfter is not null || status == HttpStatusCode.TooManyRequests || remaining == "0")
        {
            var wait = retryAfter ?? SecondaryMinimum;
            return new ForgeRateLimitedException(wait, retryAfter is { } delay ? now + delay : null, EForgeRateLimitKind.Secondary,
                $"GitHub's secondary rate limit was hit on {what}; retrying after {wait.TotalSeconds:0} s.");
        }
        return null;
    }

    /// <summary><see cref="Classify(HttpStatusCode, Func{string, string?}, string?, DateTimeOffset, string)"/> over a response.</summary>
    public static ForgeRateLimitedException? Classify(HttpResponseMessage response, string? message, DateTimeOffset now, string what)
        => Classify(response.StatusCode, name => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null, message, now, what);

    /// <summary>The rate limit an Octokit failure is, or null. Octokit's own <c>RateLimitExceededException</c> and
    /// <c>SecondaryRateLimitExceededException</c> are classified from the same response as any other 403.</summary>
    public static ForgeRateLimitedException? FromOctokit(Octokit.ApiException exception, DateTimeOffset now, string what)
    {
        var headers = exception.HttpResponse?.Headers;
        string? Header(string name) => headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        var message = exception.ApiError?.Message ?? exception.Message;
        var limited = Classify(exception.StatusCode, Header, message, now, what);
        if (limited is null && exception is Octokit.RateLimitExceededException primary)
            limited = new ForgeRateLimitedException(Max(primary.Reset - now + ResetSkew, TimeSpan.Zero), primary.Reset, EForgeRateLimitKind.Primary,
                $"GitHub's rate limit is spent on {what}; it resets at {primary.Reset:HH:mm:ss} UTC.");
        if (limited is null && exception is Octokit.SecondaryRateLimitExceededException)
            limited = new ForgeRateLimitedException(SecondaryMinimum, null, EForgeRateLimitKind.Secondary, $"GitHub's secondary rate limit was hit on {what}.");
        return limited is null ? null : new ForgeRateLimitedException(limited.Delay, limited.ResetsAtUtc, limited.Kind, limited.Message, exception);
    }

    /// <summary>A GraphQL answer's <c>errors</c> that say RATE_LIMITED, or null. The reset, when the response's headers
    /// carry one, is honoured; otherwise a minute.</summary>
    public static ForgeRateLimitedException? FromGraphQLErrors(IEnumerable<(string? Type, string? Message)> errors, Func<string, string?> header, DateTimeOffset now, string what)
    {
        var limited = errors.FirstOrDefault(e => string.Equals(e.Type, "RATE_LIMITED", StringComparison.Ordinal));
        if (limited.Type is null)
            return null;
        var reset = long.TryParse(header("x-ratelimit-reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : (DateTimeOffset?)null;
        var wait = RetryAfter(header("retry-after"), now) ?? (reset is { } at ? Max(at - now + ResetSkew, TimeSpan.Zero) : SecondaryMinimum);
        return new ForgeRateLimitedException(wait, reset, EForgeRateLimitKind.GraphQL,
            $"GitHub's GraphQL rate limit is spent on {what}: {limited.Message ?? "RATE_LIMITED"}.");
    }

    /// <summary><c>retry-after</c> in seconds or as an HTTP date; null when absent or unreadable.</summary>
    private static TimeSpan? RetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? Max(at - now, TimeSpan.Zero) : null;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
