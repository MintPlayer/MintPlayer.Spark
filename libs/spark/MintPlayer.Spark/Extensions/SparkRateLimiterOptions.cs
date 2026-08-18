namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Configures the fixed-window rate limiter wired by
/// <see cref="SparkBuilderRateLimiterExtensions.AddRateLimiter"/>. Apps that want
/// the default (150 requests / 10 seconds per client IP, scoped to <c>/spark</c> and
/// <c>/connect</c>) can pass <c>_ =&gt; { }</c> — any unset property falls back to the
/// documented default.
/// </summary>
public class SparkRateLimiterOptions
{
    /// <summary>Requests allowed per window, per client IP. Defaults to 150.</summary>
    public int PermitLimit { get; set; } = 150;

    /// <summary>Window length. Defaults to 10 seconds.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The path prefixes the limiter meters. Anything outside them is not throttled at all, so
    /// static assets and unrelated application routes never compete for the budget.
    /// <para>
    /// Defaults to Spark's own anonymous surfaces: <c>/spark</c> (the framework endpoints) and
    /// <c>/connect</c> (interactive login, two-factor and consent, when the identity provider is
    /// in use). An app with its own anonymous or expensive-to-authenticate surface should add it
    /// here rather than declare a second limiter — see the remarks on
    /// <see cref="SparkBuilderRateLimiterExtensions.AddRateLimiter"/> for why a second
    /// <c>UseRateLimiter()</c> is not a safe way to do that.
    /// </para>
    /// <para>
    /// Assigning <em>replaces</em> the defaults; include <c>/spark</c> explicitly if Spark's own
    /// endpoints should stay metered. Prefixes match whole path segments, so <c>/api</c> covers
    /// <c>/api/browse</c> but not <c>/apidocs</c>. Leading and trailing slashes are normalized —
    /// <c>"api/browse"</c>, <c>"/api/browse"</c> and <c>"/api/browse/"</c> are equivalent.
    /// </para>
    /// <para>
    /// All prefixes share one bucket per client IP, exactly as <c>/spark</c> and <c>/connect</c>
    /// already do: the budget is a per-caller allowance, not a per-route one. For a distinct budget
    /// on one route, declare a named ASP.NET rate-limiting policy and attach it to that endpoint
    /// with <c>[EnableRateLimiting]</c>; this option decides <em>scope</em>, not per-route cost.
    /// </para>
    /// <para>
    /// Must not be empty. A limiter asked to meter nothing is a security control that silently does
    /// nothing, so <see cref="SparkBuilderRateLimiterExtensions.AddRateLimiter"/> throws instead.
    /// </para>
    /// </summary>
    public string[] PathPrefixes { get; set; } = ["/spark", "/connect"];
}
