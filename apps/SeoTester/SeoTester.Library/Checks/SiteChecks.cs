using System.Globalization;
using System.Text.RegularExpressions;
using SeoTester.Crawling;
using SeoTester.Scoring;
using SeoTester.Tls;

namespace SeoTester.Checks;

/// <summary>
/// The checks measured once per run by the probe (<c>docs/seo_analyzer_PRD.md</c> §4.3: robots.txt, sitemaps, TLS,
/// redirects, well-known files, DNS, RDAP, HSTS preload, the home page's assets). Pure over <see cref="SiteFacts"/>.
/// </summary>
public static partial class SiteChecks
{
    public static IReadOnlyList<string> Ids { get; } =
    [
        "technical.robots-blocks-all", "technical.robots-txt", "technical.sitemap", "technical.sitemap-stylesheet", "technical.favicon-ico",
        "technical.soft-404", "technical.opensearch",
        "security.https", "security.certificate-expiry", "security.http-redirect", "security.hsts-preload", "security.tls-legacy", "security.tls13",
        "security.security-txt", "security.change-password-url", "security.directory-listing", "security.exposed-files",
        "performance.http2", "performance.http3", "performance.compression", "performance.cache-control", "performance.minification", "performance.font-display",
        "accessibility.dark-mode", "accessibility.reduced-motion",
        "mobile.manifest", "mobile.icons",
        "domain.expiry", "domain.caa", "domain.dnssec", "domain.spf", "domain.dmarc", "domain.mta-sts", "domain.ipv6", "domain.nameservers", "domain.www-apex",
    ];

    public static IReadOnlyList<CheckFinding> Evaluate(SiteFacts facts)
    {
        var findings = new List<CheckFinding>();
        void Add(CheckFinding? finding)
        {
            if (finding is not null)
                findings.Add(finding);
        }

        // ---- robots.txt and sitemaps -----------------------------------------------------------------------------
        if (facts.Robots is { } robots)
        {
            Add(robots.Availability == RobotsAvailability.Unreachable
                ? Finding.Fail("technical.robots-blocks-all", ("status", robots.Status?.ToString(CultureInfo.InvariantCulture) ?? "no answer"))
                : robots.Rules.DisallowsAllForEveryone || robots.Rules.DisallowsAllForGooglebot
                    ? Finding.Fail("technical.robots-blocks-all", ("status", robots.Status))
                    : Finding.Pass("technical.robots-blocks-all"));
            var problems = new List<string>();
            if (robots.Availability == RobotsAvailability.Unavailable)
                problems.Add($"none found ({robots.Status})");
            if (robots.Bytes > RobotsTxt.MaxBytes)
                problems.Add("larger than 500 KiB, the rest is ignored");
            if (robots.Rules.UnknownLines > 0)
                problems.Add($"{robots.Rules.UnknownLines} unrecognised lines");
            if (robots.Availability == RobotsAvailability.Available && robots.Rules.Sitemaps.Count == 0)
                problems.Add("no Sitemap line");
            Add(problems.Count == 0 ? Finding.Pass("technical.robots-txt")
                : Finding.Make("technical.robots-txt", robots.Availability == RobotsAvailability.Unavailable ? CheckStatus.Warn : CheckStatus.Warn, [("value", string.Join("; ", problems))]));
        }
        else
        {
            Add(Finding.Error("technical.robots-blocks-all"));
            Add(Finding.Error("technical.robots-txt"));
        }

        if (facts.Sitemap is { } sitemap)
        {
            Add(sitemap.Url is null ? Finding.Fail("technical.sitemap", ("n", 0), ("value", "no sitemap found"))
                : sitemap.Error is not null ? Finding.Fail("technical.sitemap", ("n", 0), ("value", sitemap.Error), ("target", sitemap.Url))
                : sitemap.Urls > SitemapContent.ProtocolMaxUrls ? Finding.Warn("technical.sitemap", ("n", sitemap.Urls), ("value", "more than 50 000 URLs in one file"))
                : Finding.Pass("technical.sitemap", ("n", sitemap.Urls)));
            Add(sitemap.Url is null ? Finding.NotApplicable("technical.sitemap-stylesheet")
                : sitemap.HasStylesheet ? Finding.Warn("technical.sitemap-stylesheet") : Finding.Pass("technical.sitemap-stylesheet"));
        }

        Add(Fetched(facts.Favicon, "technical.favicon-ico", f => f.Ok && !(f.ContentType ?? "").Contains("html", StringComparison.OrdinalIgnoreCase)
            ? Finding.Pass("technical.favicon-ico") : Finding.Warn("technical.favicon-ico", ("status", f.Status?.ToString(CultureInfo.InvariantCulture) ?? f.Error))));
        Add(Fetched(facts.NotFoundProbe, "technical.soft-404", f => f.Status is 404 or 410
            ? Finding.Pass("technical.soft-404") : Finding.Fail("technical.soft-404", ("status", f.Status))));
        Add(facts.OpenSearch is null ? Finding.Pass("technical.opensearch", ("value", "none"))
            : facts.OpenSearch.Ok && (facts.OpenSearch.Body ?? "").Contains("OpenSearchDescription", StringComparison.Ordinal)
                ? Finding.Pass("technical.opensearch", ("value", "valid"))
                : Finding.Warn("technical.opensearch", ("value", $"invalid ({facts.OpenSearch.Status?.ToString(CultureInfo.InvariantCulture) ?? facts.OpenSearch.Error})")));

        // ---- TLS and HTTPS ---------------------------------------------------------------------------------------
        if (!facts.IsHttps)
        {
            Add(Finding.Fail("security.https", ("value", "the site's address is http://")));
            foreach (var id in new[] { "security.certificate-expiry", "security.tls-legacy", "security.tls13", "security.http-redirect", "security.hsts-preload" })
                Add(Finding.NotApplicable(id));
        }
        else if (facts.Tls is { } tls)
        {
            Add(!tls.Handshake ? Finding.Fail("security.https", ("value", tls.Error ?? "no TLS handshake"))
                : tls.CertificateProblem is { } problem ? Finding.Fail("security.https", ("value", problem))
                : Finding.Pass("security.https"));
            if (tls.CertificateNotAfter is { } notAfter)
            {
                var days = (int)Math.Floor((notAfter - facts.NowUtc).TotalDays);
                Add(days < 14 ? Finding.Fail("security.certificate-expiry", ("days", days), ("value", notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                    : days < 30 ? Finding.Warn("security.certificate-expiry", ("days", days))
                    : Finding.Pass("security.certificate-expiry", ("days", days)));
            }
            else
                Add(Finding.Error("security.certificate-expiry", "no certificate"));
            var legacy = new List<string>();
            if (tls.Tls10 == LegacyTlsAnswer.Accepted) legacy.Add("TLS 1.0");
            if (tls.Tls11 == LegacyTlsAnswer.Accepted) legacy.Add("TLS 1.1");
            Add(legacy.Count > 0 ? Finding.Fail("security.tls-legacy", ("value", string.Join(" and ", legacy)))
                : tls.Tls10 == LegacyTlsAnswer.Inconclusive && tls.Tls11 == LegacyTlsAnswer.Inconclusive ? Finding.Error("security.tls-legacy", "no answer")
                : Finding.Pass("security.tls-legacy"));
            Add(!tls.Handshake ? Finding.Error("security.tls13") : tls.Tls13 ? Finding.Pass("security.tls13") : Finding.Warn("security.tls13"));
        }
        else
            Add(Finding.Error("security.https"));

        if (facts.IsHttps && facts.HttpToHttps is { } http)
        {
            // The first hop must be permanent and land on https on the same host (the HSTS preload rule, research §1.4).
            var host = facts.Home.IdnHost;
            var oneHop = http.Hops.Count >= 1 && http.Hops[0].Status is 301 or 308
                && Uri.TryCreate(http.Hops.Count > 1 ? http.Hops[1].Url : http.FinalUrl, UriKind.Absolute, out var next)
                && next.Scheme == Uri.UriSchemeHttps && string.Equals(next.IdnHost, new Uri(http.Requested).IdnHost, StringComparison.OrdinalIgnoreCase);
            Add(http.Error is not null && http.Hops.Count == 0 ? Finding.Error("security.http-redirect", http.Error)
                : oneHop ? Finding.Pass("security.http-redirect")
                : Finding.Fail("security.http-redirect", ("value", host)));
        }
        if (facts.IsHttps)
            Add(facts.HstsPreloadFailed ? Finding.Error("security.hsts-preload")
                : facts.HstsPreloadStatus is null ? null
                : facts.HstsPreloadStatus == "preloaded" ? Finding.Pass("security.hsts-preload", ("value", "preloaded"))
                : Finding.Warn("security.hsts-preload", ("value", facts.HstsPreloadStatus)));

        // ---- Well-known files and probes for the owner ------------------------------------------------------------
        Add(Fetched(facts.SecurityTxt, "security.security-txt", SecurityTxt(facts.NowUtc)));
        Add(!facts.HomeHasLoginForm ? Finding.NotApplicable("security.change-password-url")
            : Fetched(facts.ChangePassword, "security.change-password-url", f =>
                (f.Status is >= 300 and < 400 && f.Location is not null) || f.Ok ? Finding.Pass("security.change-password-url") : Finding.Warn("security.change-password-url")));
        if (!facts.Intrusive)
        {
            Add(Finding.NotApplicable("security.exposed-files"));
            Add(Finding.NotApplicable("security.directory-listing"));
        }
        else
        {
            var exposed = facts.ExposedFiles.FirstOrDefault(IsExposed);
            Add(exposed is not null ? Finding.Fail("security.exposed-files", ("target", exposed.Url))
                : facts.ExposedFiles.Count == 0 ? Finding.NotApplicable("security.exposed-files") : Finding.Pass("security.exposed-files"));
            var listing = facts.Directories.FirstOrDefault(d => d.Ok && d.Body is { } b && DirectoryListing().IsMatch(b));
            Add(listing is not null ? Finding.Fail("security.directory-listing", ("target", listing.Url))
                : facts.Directories.Count == 0 ? Finding.NotApplicable("security.directory-listing") : Finding.Pass("security.directory-listing"));
        }

        // ---- Protocol and assets ---------------------------------------------------------------------------------
        if (facts.IsHttps && facts.Tls is { Handshake: true } t)
            Add(t.Alpn == "h2" || facts.HomeHttpVersion?.Major >= 2 ? Finding.Pass("performance.http2") : Finding.Fail("performance.http2"));
        else
            Add(Finding.NotApplicable("performance.http2"));
        Add(facts.HomeAltSvc is { } altSvc && altSvc.Contains("h3", StringComparison.OrdinalIgnoreCase)
            ? Finding.Pass("performance.http3", ("value", "advertised (Alt-Svc)"))
            : Finding.Warn("performance.http3", ("value", "not advertised")));

        var text = facts.Assets.Where(a => a.Status is >= 200 and < 300).ToList();
        var uncompressed = text.Count(a => a.Bytes >= 1024 && !PageChecks.IsCompressed(a.WireEncoding));
        Add(text.Count == 0 ? null
            : uncompressed > 0 ? Finding.Fail("performance.compression", ("n", uncompressed), ("target", text.First(a => a.Bytes >= 1024 && !PageChecks.IsCompressed(a.WireEncoding)).Url))
            : Finding.Pass("performance.compression"));
        var shortCache = text.Count(a => !LongLived(a.CacheControl));
        Add(text.Count == 0 ? Finding.NotApplicable("performance.cache-control")
            : shortCache > 0 ? Finding.Warn("performance.cache-control", ("n", shortCache)) : Finding.Pass("performance.cache-control"));
        var judged = text.Where(a => a.Minified is not null).ToList();
        var unminified = judged.Where(a => a.Minified == false).ToList();
        Add(judged.Count == 0 ? Finding.NotApplicable("performance.minification")
            : unminified.Count > 0 ? Finding.Warn("performance.minification", ("n", unminified.Count), ("value", $"{unminified.Sum(a => a.SavingBytes) / 1024} KB"))
            : Finding.Pass("performance.minification"));

        if (facts.Css is { } css)
        {
            Add(css.FontFaces == 0 ? Finding.NotApplicable("performance.font-display")
                : css.FontFacesWithoutDisplay > 0 ? Finding.Warn("performance.font-display", ("n", css.FontFacesWithoutDisplay)) : Finding.Pass("performance.font-display"));
            Add(css.DarkMode ? Finding.Pass("accessibility.dark-mode", ("value", "yes")) : Finding.Warn("accessibility.dark-mode", ("value", "no")));
            Add(css.ReducedMotion ? Finding.Pass("accessibility.reduced-motion", ("value", "yes")) : Finding.Warn("accessibility.reduced-motion", ("value", "no")));
        }

        // ---- Manifest and icons ----------------------------------------------------------------------------------
        Add(facts.Manifest is null ? Finding.Warn("mobile.manifest", ("value", "no manifest link"))
            : facts.Manifest.Problems.Count == 0 ? Finding.Pass("mobile.manifest")
            : Finding.Warn("mobile.manifest", ("value", string.Join(", ", facts.Manifest.Problems))));
        var iconProblems = new List<string>();
        if (!facts.HasIconLink && facts.Favicon is not { Ok: true })
            iconProblems.Add("no icon");
        if (!facts.HasAppleTouchIcon)
            iconProblems.Add("no apple-touch-icon");
        Add(iconProblems.Count == 0 ? Finding.Pass("mobile.icons") : Finding.Warn("mobile.icons", ("value", string.Join(", ", iconProblems))));

        // ---- Domain and DNS --------------------------------------------------------------------------------------
        Add(Domain(facts));
        if (facts.Dns is { } dns)
            findings.AddRange(DnsFindings(dns, facts.Tls?.CertificateIssuer));
        else
            foreach (var id in new[] { "domain.caa", "domain.dnssec", "domain.spf", "domain.dmarc", "domain.mta-sts", "domain.ipv6", "domain.nameservers" })
                Add(Finding.Error(id));
        if (facts.WwwVariant is { } www)
        {
            var main = facts.Home.IdnHost;
            Add(www.Error is not null && www.Hops.Count == 0 ? Finding.Fail("domain.www-apex", ("target", www.Requested))
                : www.FinalUrl is { } final && Uri.TryCreate(final, UriKind.Absolute, out var u) && string.Equals(u.IdnHost, main, StringComparison.OrdinalIgnoreCase) && www.Hops.Count > 0
                    ? Finding.Pass("domain.www-apex")
                    : Finding.Fail("domain.www-apex", ("target", www.Requested)));
        }
        return findings;
    }

    private static CheckFinding Fetched(FetchFacts? fetched, string id, Func<FetchFacts, CheckFinding> judge)
        => fetched is null ? Finding.NotApplicable(id)
            : fetched.Status is null ? Finding.Error(id, fetched.Error)
            : judge(fetched);

    private static Func<FetchFacts, CheckFinding> SecurityTxt(DateTime now) => f =>
    {
        if (!f.Ok || f.Body is null || !(f.ContentType ?? "").Contains("text/plain", StringComparison.OrdinalIgnoreCase))
            return Finding.Warn("security.security-txt", ("value", "missing"));
        var lines = f.Body.Split('\n').Select(l => l.Trim()).ToList();
        var contact = lines.Any(l => l.StartsWith("Contact:", StringComparison.OrdinalIgnoreCase));
        var expires = lines.FirstOrDefault(l => l.StartsWith("Expires:", StringComparison.OrdinalIgnoreCase));
        if (!contact || expires is null)
            return Finding.Warn("security.security-txt", ("value", !contact ? "no Contact field" : "no Expires field"));
        return DateTimeOffset.TryParse(expires["Expires:".Length..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && when.UtcDateTime < now
            ? Finding.Warn("security.security-txt", ("value", when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            : Finding.Pass("security.security-txt");
    };

    /// <summary>The probe found a sensitive file: a 200 whose body looks like the file, not like an HTML error page.</summary>
    private static bool IsExposed(FetchFacts file)
    {
        if (!file.Ok || file.Body is null || (file.ContentType ?? "").Contains("text/html", StringComparison.OrdinalIgnoreCase))
            return false;
        var path = new Uri(file.Url).AbsolutePath;
        return path switch
        {
            "/.git/HEAD" => file.Body.StartsWith("ref:", StringComparison.Ordinal) || HexSha().IsMatch(file.Body.Trim()),
            "/.env" => EnvLine().IsMatch(file.Body),
            "/.DS_Store" => file.Body.Contains("Bud1", StringComparison.Ordinal),
            "/web.config" => file.Body.Contains("<configuration", StringComparison.OrdinalIgnoreCase),
            "/phpinfo.php" => file.Body.Contains("PHP Version", StringComparison.Ordinal),
            "/.htpasswd" => HtpasswdLine().IsMatch(file.Body),
            _ => false,
        };
    }

    /// <summary>The paths of the exposed-files probe (verified sites only, D11).</summary>
    public static IReadOnlyList<string> ExposedFilePaths { get; } = ["/.git/HEAD", "/.env", "/.DS_Store", "/web.config", "/phpinfo.php", "/.htpasswd"];

    [GeneratedRegex(@"^[0-9a-f]{40}$", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex HexSha();

    [GeneratedRegex(@"^\s*[A-Z_][A-Z0-9_]*\s*=", RegexOptions.Multiline, matchTimeoutMilliseconds: 500)]
    private static partial Regex EnvLine();

    [GeneratedRegex(@"^[^:\s]+:\$?[\w./$]{10,}", RegexOptions.Multiline, matchTimeoutMilliseconds: 500)]
    private static partial Regex HtpasswdLine();

    [GeneratedRegex(@"<title>\s*Index of /|\[To Parent Directory\]|<h1>Index of /", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex DirectoryListing();

    /// <summary>A static asset's Cache-Control keeps it at least 30 days (a year plus immutable is best).</summary>
    public static bool LongLived(string? cacheControl)
    {
        if (cacheControl is null || cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase) || cacheControl.Contains("no-cache", StringComparison.OrdinalIgnoreCase))
            return false;
        var match = MaxAgeValue().Match(cacheControl);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 30L * 24 * 3600;
    }

    [GeneratedRegex(@"max-age\s*=\s*(\d+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex MaxAgeValue();

    private static CheckFinding Domain(SiteFacts facts)
    {
        if (facts.Rdap is not { } rdap)
            return Finding.Error("domain.expiry");
        if (rdap.State is "no-rdap" or "not-published")
            return Finding.NotApplicable("domain.expiry");
        if (rdap.Expires is not { } expires)
            return Finding.Error("domain.expiry", rdap.State);
        var days = (int)Math.Floor((expires - facts.NowUtc).TotalDays);
        var date = expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return days < 30 ? Finding.Fail("domain.expiry", ("days", days), ("value", date))
            : days < 365 ? Finding.Warn("domain.expiry", ("days", days), ("value", date))
            : Finding.Pass("domain.expiry", ("days", days), ("value", date));
    }

    private static IEnumerable<CheckFinding> DnsFindings(DnsFacts dns, string? issuer)
    {
        // CAA (RFC 8659): absent is allowed (any CA may issue); present must allow the current certificate's CA.
        if (dns.Caa is null)
            yield return Finding.Error("domain.caa");
        else if (dns.Caa.Count == 0)
            yield return Finding.Warn("domain.caa", ("value", "no CAA record"));
        else
        {
            var issuers = dns.Caa.Select(r => CaaIssue().Match(r)).Where(m => m.Success).Select(m => m.Groups[1].Value.Trim().Trim('"').ToLowerInvariant()).ToList();
            var allows = issuer is null || issuers.Count == 0 || issuers.Any(i => i.Length > 0 && IssuerMatches(issuer, i));
            yield return allows ? Finding.Pass("domain.caa", ("value", string.Join(", ", issuers)))
                : Finding.Fail("domain.caa", ("value", issuer));
        }

        yield return dns.Signed is null ? Finding.Error("domain.dnssec") : dns.Signed.Value ? Finding.Pass("domain.dnssec") : Finding.Warn("domain.dnssec");

        if (dns.ApexTxt is null)
            yield return Finding.Error("domain.spf");
        else
        {
            var spf = dns.ApexTxt.Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
            yield return spf.Count == 1 && !spf[0].Contains("+all", StringComparison.OrdinalIgnoreCase) ? Finding.Pass("domain.spf", ("value", spf[0]))
                : Finding.Fail("domain.spf", ("value", spf.Count > 1 ? "several SPF records" : spf.Count == 1 ? "+all allows everyone" : "none"));
        }

        if (dns.DmarcTxt is null)
            yield return Finding.Error("domain.dmarc");
        else
        {
            var dmarc = dns.DmarcTxt.FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
            yield return dmarc is null ? Finding.Fail("domain.dmarc")
                : dmarc.Contains("p=none", StringComparison.OrdinalIgnoreCase) ? Finding.Warn("domain.dmarc", ("value", dmarc))
                : Finding.Pass("domain.dmarc", ("value", dmarc));
        }

        yield return dns.HasMx is false ? Finding.NotApplicable("domain.mta-sts")
            : dns.MtaStsTxt is null ? Finding.Error("domain.mta-sts")
            : dns.MtaStsTxt.Any(t => t.StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase)) ? Finding.Pass("domain.mta-sts")
            : Finding.Warn("domain.mta-sts");

        yield return dns.Aaaa is null ? Finding.Error("domain.ipv6")
            : dns.Aaaa.Count > 0 ? Finding.Pass("domain.ipv6", ("value", "yes")) : Finding.Warn("domain.ipv6", ("value", "no"));

        if (dns.Nameservers is null)
            yield return Finding.Error("domain.nameservers");
        else
        {
            var networks = (dns.NameserverAddresses ?? []).Select(NetworkOf).Distinct().Count();
            if (networks == 0)
                networks = dns.Nameservers.Select(n => string.Join('.', n.TrimEnd('.').Split('.').TakeLast(2))).Distinct().Count();
            yield return dns.Nameservers.Count >= 2 && networks >= 2 ? Finding.Pass("domain.nameservers", ("n", dns.Nameservers.Count), ("k", networks))
                : Finding.Warn("domain.nameservers", ("n", dns.Nameservers.Count), ("k", networks));
        }
    }

    /// <summary>A /24 (IPv4) or /48 (IPv6) as the "network" of a nameserver address.</summary>
    private static string NetworkOf(string address)
    {
        if (System.Net.IPAddress.TryParse(address, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}" : Convert.ToHexString(bytes, 0, 6);
        }
        return address;
    }

    [GeneratedRegex(@"\bissue(?:wild)?\s+""?([^;""\s]*)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex CaaIssue();

    /// <summary>Whether a CAA <c>issue</c> domain (letsencrypt.org) names the CA in the certificate's issuer (O=Let's Encrypt).</summary>
    private static bool IssuerMatches(string issuer, string caaDomain)
    {
        var label = caaDomain.Split('.')[0];
        var simplifiedIssuer = new string(issuer.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return label.Length >= 3 && simplifiedIssuer.Contains(new string(label.Where(char.IsLetterOrDigit).ToArray()), StringComparison.Ordinal)
            || (caaDomain.Contains("pki.goog") && simplifiedIssuer.Contains("google"))
            || (caaDomain.Contains("amazon") && simplifiedIssuer.Contains("amazon"))
            || (caaDomain.Contains("digicert") && simplifiedIssuer.Contains("digicert"))
            || (caaDomain.Contains("sectigo") && (simplifiedIssuer.Contains("sectigo") || simplifiedIssuer.Contains("comodo")));
    }
}
