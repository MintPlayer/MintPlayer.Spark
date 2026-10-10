namespace MintPlayer.Spark.Http;

/// <summary>
/// Limits of one SSRF-safe HTTP client (<c>docs/seo_analyzer_PRD.md</c> X2). Every value has a safe
/// default; an application only widens what it needs, and only per client name.
/// <para>
/// Bound from configuration where the registering call names a section (<c>Spark:SafeHttp</c> for
/// <c>spark.AddSafeHttp()</c>). The binder <b>appends</b> to the list properties, so configured ports and
/// networks are added to the defaults, never replace them.
/// </para>
/// </summary>
public sealed class SafeHttpOptions
{
    /// <summary>Destination ports a request or a redirect may use. Default 80 and 443.</summary>
    public List<int> AllowedPorts { get; set; } = [80, 443];

    /// <summary>
    /// Networks (CIDR, e.g. <c>127.0.0.1/32</c>) allowed although they are not global unicast. Empty by
    /// default. Meant for a test server or a development peer on loopback; never for production.
    /// </summary>
    public List<string> AllowedNetworks { get; set; } = [];

    /// <summary>
    /// Networks (CIDR) refused although they are global unicast. Checked before <see cref="AllowedNetworks"/>,
    /// and also against the IPv4 address embedded in NAT64, 6to4 and Teredo addresses.
    /// </summary>
    public List<string> BlockedNetworks { get; set; } = [];

    /// <summary>Most bytes read from the wire for one response, before decompression. Default 10 MiB.</summary>
    public long MaxResponseBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Most bytes a compressed response may expand to (decompression bombs). Default 50 MiB.</summary>
    public long MaxDecompressedBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    /// The whole exchange: every redirect hop and, for the default buffered read, the body. Default 30 s.
    /// A caller that reads with <c>ResponseHeadersRead</c> bounds the body with its own token.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>One TCP (+ TLS) connect. Default 10 s.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Redirects followed, each re-checked (scheme, port, address). 0 refuses any redirect. Default 5.</summary>
    public int MaxRedirects { get; set; } = 5;
}
