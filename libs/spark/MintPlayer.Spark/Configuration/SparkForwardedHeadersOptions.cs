namespace MintPlayer.Spark.Configuration;

/// <summary>
/// Which reverse proxies Spark believes about the caller's address and scheme. Bound from
/// <c>Spark:ForwardedHeaders</c>; Spark adds <c>UseForwardedHeaders()</c> to the front of the
/// pipeline itself, so an application neither configures nor calls it (#460, D15).
/// </summary>
/// <remarks>
/// <para>
/// Why the framework owns this: every demo app used to clear <c>KnownNetworks</c> and
/// <c>KnownProxies</c>, which does not mean "no proxies" — it disables peer validation, so
/// <c>X-Forwarded-For</c> was taken from whoever sent it. Every rate limiter partitions on the
/// resulting address, so any caller could pick a fresh partition per request (#436).
/// </para>
/// <para>
/// <b>Default trust</b>: loopback (<c>127.0.0.0/8</c>, <c>::1</c>) and the private ranges
/// (<c>10.0.0.0/8</c>, <c>172.16.0.0/12</c>, <c>192.168.0.0/16</c>, <c>fc00::/7</c>) — right for
/// an application reachable only through a proxy on a private network (a Docker bridge, a
/// Kubernetes pod network). ⚠️ It is <em>wrong</em> for an application exposed directly on a
/// private network to untrusted clients, and for one behind a CDN whose edge addresses are public:
/// name the proxies there.
/// </para>
/// <para>
/// Only <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are honoured by default.
/// <c>X-Forwarded-Host</c> is opt-in through <see cref="ForwardHost"/>.
/// </para>
/// </remarks>
public sealed class SparkForwardedHeadersOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Spark:ForwardedHeaders";

    /// <summary>
    /// Trusted proxy networks in CIDR form (<c>10.0.0.0/8</c>). Setting this or
    /// <see cref="KnownProxies"/> <em>replaces</em> the default trust list — loopback included —
    /// rather than adding to it.
    /// </summary>
    public string[]? KnownNetworks { get; set; }

    /// <summary>Trusted individual proxy addresses. See <see cref="KnownNetworks"/>.</summary>
    public string[]? KnownProxies { get; set; }

    /// <summary>
    /// How many proxies sit between the internet and this process — ASP.NET Core's
    /// <c>ForwardLimit</c>. Defaults to 1 and is never unlimited: with one hop only the rightmost
    /// <c>X-Forwarded-For</c> entry (the one the entry proxy appended) is read, so entries a client
    /// wrote to its left are ignored rather than merely validated.
    /// </summary>
    public int ProxyHops { get; set; } = 1;

    /// <summary>
    /// Also honour <c>X-Forwarded-Host</c>. Off by default, and refused at startup while
    /// <c>AllowedHosts</c> is <c>*</c>: a forwarded host feeds every absolute URL the application
    /// builds (redirects, email links), so it has to be restricted to the hosts the application
    /// serves. Most proxies pass the original <c>Host</c> header through anyway, which needs none
    /// of this.
    /// </summary>
    public bool ForwardHost { get; set; }
}
