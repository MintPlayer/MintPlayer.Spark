namespace MintPlayer.Spark.Http;

/// <summary>
/// Limits of one SSRF-safe HTTP client (<c>docs/seo_analyzer_PRD.md</c> X2). Every value has a safe
/// default; an application only widens what it needs, and only per client name.
/// <para>
/// Bound from configuration where the registering call names a section (<c>Spark:SafeHttp</c> for
/// <c>spark.AddSafeHttp()</c>). A list the section names <b>replaces</b> the list beneath it rather than
/// appending to it (<c>docs/seo_analyzer_PRD.md</c> D39), so configuration can remove the default ports:
/// <c>"AllowedPorts": [ 443 ]</c> allows 443 only, and <c>"AllowedPorts": [ 443, 8443 ]</c> adds 8443 to
/// HTTPS only. A list the section does not name keeps its default.
/// </para>
/// </summary>
public sealed class SafeHttpOptions
{
    /// <summary>
    /// Destination ports a request or a redirect may use. Default 80 and 443. A configured list replaces
    /// the default; code that only adds a port calls <c>AllowedPorts.Add</c>.
    /// </summary>
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

    /// <summary>
    /// When <see langword="false"/>, a redirect is returned to the caller as it is (status and <c>Location</c>)
    /// instead of being followed, and <see cref="MaxRedirects"/> does not apply. The caller that follows it sends a
    /// new request, which is checked like any other. Meant for a caller that follows redirects itself, such as a
    /// headless browser whose requests are fulfilled through this client (<c>docs/seo_analyzer_PRD.md</c> §6, M10).
    /// Default <see langword="true"/>.
    /// </summary>
    public bool FollowRedirects { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, a redirect is followed only when it stays on the origin (scheme, host and
    /// port) of the URI the caller requested; a redirect to any other origin is refused with
    /// <see cref="SafeHttpBlockReason.CrossOriginRedirect"/>. Every hop is still checked like any other
    /// (scheme, port, address). Meant for requests that carry a credential in the body, such as a
    /// back-channel logout token, which a cross-origin hop would resend to a URI nobody registered
    /// (<c>docs/seo_analyzer_PRD.md</c> D40). Default <see langword="false"/>.
    /// </summary>
    public bool RedirectsWithinOriginOnly { get; set; }
}
