using SeoTester.Crawling;
using SeoTester.Tls;

namespace SeoTester.Checks;

/// <summary>
/// What the probe (<c>seo-probe</c>, <c>docs/seo_analyzer_PRD.md</c> §4.3) learned about a site once, as plain data.
/// The probe fills it from the network; <see cref="SiteChecks"/> judges it without I/O. A null part was not measured
/// (its checks are N/A, or Error when the measurement failed).
/// </summary>
public sealed class SiteFacts
{
    public required Uri Home { get; init; }
    public required DateTime NowUtc { get; init; }
    public bool IsHttps => Home.Scheme == Uri.UriSchemeHttps;

    /// <summary>Probes that only the owner may run (the IntrusiveProbes gate, D11).</summary>
    public bool Intrusive { get; init; }

    public RobotsFacts? Robots { get; set; }
    public SitemapFacts? Sitemap { get; set; }
    public TlsFacts? Tls { get; set; }

    /// <summary><c>http://host/</c> for an https site: where it went.</summary>
    public RedirectFacts? HttpToHttps { get; set; }

    /// <summary>The other of www / apex: whether it resolves and redirects to the site.</summary>
    public RedirectFacts? WwwVariant { get; set; }

    public Version? HomeHttpVersion { get; set; }
    public string? HomeAltSvc { get; set; }
    public bool HomeHasLoginForm { get; set; }

    public FetchFacts? SecurityTxt { get; set; }
    public FetchFacts? ChangePassword { get; set; }
    public FetchFacts? Favicon { get; set; }
    public FetchFacts? NotFoundProbe { get; set; }

    public List<FetchFacts> ExposedFiles { get; set; } = [];
    public List<FetchFacts> Directories { get; set; } = [];

    public ManifestFacts? Manifest { get; set; }
    public bool HasIconLink { get; set; }
    public bool HasAppleTouchIcon { get; set; }
    public FetchFacts? OpenSearch { get; set; }

    public CssFacts? Css { get; set; }
    public List<AssetFacts> Assets { get; set; } = [];

    public DnsFacts? Dns { get; set; }
    public RdapFacts? Rdap { get; set; }

    /// <summary>hstspreload.org's status (<c>unknown</c>, <c>pending</c>, <c>preloaded</c>, <c>rejected</c>), null when not asked.</summary>
    public string? HstsPreloadStatus { get; set; }
    public bool HstsPreloadFailed { get; set; }
}

public sealed record RobotsFacts(RobotsAvailability Availability, int? Status, RobotsTxt Rules, long Bytes);

/// <param name="Url">The sitemap read (from robots.txt or <c>/sitemap.xml</c>); null when none was found.</param>
public sealed record SitemapFacts(string? Url, int? Status, string? Error, int Urls, bool HasStylesheet, int ChildSitemaps, bool FromRobots);

/// <param name="Alpn">The protocol negotiated by ALPN: <c>h2</c>, <c>http/1.1</c> or null.</param>
/// <param name="CertificateProblem">Why the certificate is not trusted for the host (chain, name, expiry), null when it is.</param>
public sealed record TlsFacts(bool Handshake, string? Error, string? Protocol, string? Alpn, DateTime? CertificateNotAfter, string? CertificateProblem,
    string? CertificateIssuer, LegacyTlsAnswer? Tls10, LegacyTlsAnswer? Tls11, bool Tls13);

/// <param name="Hops">Each redirect: the URL and its status.</param>
/// <param name="FinalUrl">Where it ended; null when the request failed.</param>
public sealed record RedirectFacts(string Requested, IReadOnlyList<(string Url, int Status)> Hops, string? FinalUrl, int? FinalStatus, string? Error);

/// <param name="Body">At most a few KB of the body, for the few checks that read it.</param>
public sealed record FetchFacts(string Url, int? Status, string? ContentType, string? Body, string? Location, string? Error)
{
    public bool Ok => Status is >= 200 and < 300;
}

public sealed record ManifestFacts(string? Href, int? Status, IReadOnlyList<string> Problems);

/// <param name="FontFacesWithoutDisplay">@font-face rules and Google Fonts URLs without a font-display / display=.</param>
public sealed record CssFacts(int FontFaces, int FontFacesWithoutDisplay, bool DarkMode, bool ReducedMotion);

/// <param name="Minified">Null when not judged (too small, or not text).</param>
public sealed record AssetFacts(string Url, string Kind, int? Status, string? WireEncoding, string? CacheControl, long Bytes, bool? Minified, long SavingBytes);

/// <param name="Caa">The CAA records at the closest name that has any (RFC 8659 tree climb); empty = none; null = lookup failed.</param>
public sealed record DnsFacts(string Apex, IReadOnlyList<string>? Caa, bool? Signed, IReadOnlyList<string>? ApexTxt, IReadOnlyList<string>? DmarcTxt,
    IReadOnlyList<string>? MtaStsTxt, bool? HasMx, IReadOnlyList<string>? Aaaa, IReadOnlyList<string>? Nameservers, IReadOnlyList<string>? NameserverAddresses);

/// <param name="State"><c>found</c>, <c>not-published</c> (the registry has RDAP but no expiry), <c>no-rdap</c> (not in the bootstrap), <c>error</c>.</param>
public sealed record RdapFacts(string State, DateTime? Expires, string? Server);
