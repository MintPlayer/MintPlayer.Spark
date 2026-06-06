namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Configures the fixed-window rate limiter wired by
/// <see cref="SparkBuilderRateLimiterExtensions.AddRateLimiter"/>. Apps that want
/// the default (150 requests / 10 seconds per client IP, scoped to <c>/spark/</c>)
/// can pass <c>_ =&gt; { }</c> — any unset property falls back to the documented default.
/// </summary>
public class SparkRateLimiterOptions
{
    /// <summary>Requests allowed per window, per client IP. Defaults to 150.</summary>
    public int PermitLimit { get; set; } = 150;

    /// <summary>Window length. Defaults to 10 seconds.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(10);
}
