namespace MintPlayer.Spark.Apps;

/// <summary>How deliveries to one destination are ordered (D22). The site developer chooses.</summary>
public enum WebhookOrdering
{
    /// <summary>Every delivery is independent: a slow or failing event never holds up the next. The default.</summary>
    Unordered,

    /// <summary>
    /// One delivery in flight per destination; the next starts when it succeeds or is given up (<see cref="WebhookExhaustedPolicy"/>).
    /// A dead endpoint then delays everything behind it, so pick it only when receivers need the order.
    /// </summary>
    PerDestination,
}

/// <summary>What an ordered chain does when a delivery runs out of attempts.</summary>
public enum WebhookExhaustedPolicy
{
    /// <summary>Record the failure and move on to the next event. The default.</summary>
    Skip,

    /// <summary>Stop the chain and disable the endpoint; re-enabling it retries the failed delivery first.</summary>
    Halt,
}

/// <summary><c>Spark:Apps</c>.</summary>
public sealed class SparkAppsOptions
{
    public const string SectionName = "Spark:Apps";

    /// <summary>The catalog's title (OpenAPI <c>info.title</c>), e.g. "CodeCoverage webhooks".</summary>
    public string CatalogTitle { get; set; } = "Webhooks";

    /// <summary>The catalog's version (OpenAPI <c>info.version</c>).</summary>
    public string CatalogVersion { get; set; } = "1.0.0";

    public WebhookOrdering Ordering { get; set; } = WebhookOrdering.Unordered;

    public WebhookExhaustedPolicy OnExhausted { get; set; } = WebhookExhaustedPolicy.Skip;

    /// <summary>
    /// A larger payload is sent thin: <c>data</c> becomes <c>{ "truncated": true, "size": n }</c> and the receiver fetches
    /// the object through the API (D22). Default 256 KB.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 256 * 1024;

    /// <summary>How long the delivery log (with each payload) is kept (D23). Default 30 days.</summary>
    public TimeSpan DeliveryRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>An endpoint that fails without a single success this long is disabled (D14). Default 5 days.</summary>
    public TimeSpan AutoDisableAfter { get; set; } = TimeSpan.FromDays(5);

    /// <summary>After a secret rotation the old secret still signs (beside the new one) this long. Default 24 hours.</summary>
    public TimeSpan SecretRotationOverlap { get; set; } = TimeSpan.FromHours(24);

    /// <summary>This many consecutive failures within <see cref="BreakerWindow"/> open the destination's breaker (D14).</summary>
    public int BreakerThreshold { get; set; } = 3;

    /// <summary>While open, deliveries to the destination are not sent; each counts as a failed attempt. Default 1 minute.</summary>
    public TimeSpan BreakerWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The most endpoints one owner may register. Default 20.</summary>
    public int MaxEndpointsPerOwner { get; set; } = 20;

    /// <summary>Accept <c>http://</c> endpoint URLs. For development only; the SSRF-safe client still refuses private networks.</summary>
    public bool AllowInsecureUrls { get; set; }

    /// <summary>The User-Agent of every delivery.</summary>
    public string UserAgent { get; set; } = "Spark-Webhooks/1";
}
