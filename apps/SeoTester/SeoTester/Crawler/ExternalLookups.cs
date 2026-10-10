using Raven.Client.Documents;
using SeoTester.Checks;
using SeoTester.Entities;

namespace SeoTester.Crawler;

/// <summary>
/// The third-party lookups of the probe, each cached in a <see cref="LookupCache"/> (<c>docs/seo_analyzer_PRD.md</c>
/// §4.2): IANA's RDAP bootstrap (7 days), a domain's RDAP answer (1 day) and hstspreload.org's status (1 day, S10).
/// Spike S4 decided: RDAP through the bootstrap only, no WHOIS, no hard-coded endpoints; a registry without RDAP or
/// without an expiry is "not published", never an error.
/// </summary>
public sealed class ExternalLookups(IDocumentStore store, PageFetcher fetcher, TimeProvider time, IConfiguration configuration)
{
    /// <summary>Most bytes of a lookup answer read and cached.</summary>
    private const int MaxAnswerBytes = 512 * 1024;

    private string BootstrapUrl => configuration["SeoTester:Lookups:RdapBootstrap"] ?? "https://data.iana.org/rdap/dns.json";
    private string HstsPreloadUrl => configuration["SeoTester:Lookups:HstsPreload"] ?? "https://hstspreload.org/api/v2/status?domain=";

    public async Task<RdapFacts> RdapAsync(string domain, CancellationToken cancellationToken)
    {
        var bootstrap = await CachedAsync("rdap-bootstrap", "dns", new Uri(BootstrapUrl), TimeSpan.FromDays(7), cancellationToken);
        if (bootstrap.Value is null)
            return new RdapFacts("error", null, null);
        if (SiteParsers.RdapBaseFor(bootstrap.Value, domain) is not { } baseUrl)
            return new RdapFacts("no-rdap", null, null);
        if (!Uri.TryCreate(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), "domain/" + Uri.EscapeDataString(domain), out var url))
            return new RdapFacts("error", null, baseUrl);
        var answer = await CachedAsync("rdap", domain, url, TimeSpan.FromDays(1), cancellationToken);
        if (answer.Value is null)
            return new RdapFacts(answer.Status == 404 ? "not-published" : "error", null, url.Host);
        return SiteParsers.RdapExpiry(answer.Value) is { } expires ? new RdapFacts("found", expires, url.Host) : new RdapFacts("not-published", null, url.Host);
    }

    /// <summary>hstspreload.org's status for <paramref name="domain"/>, or null when it could not be asked.</summary>
    public async Task<string?> HstsPreloadAsync(string domain, CancellationToken cancellationToken)
    {
        var answer = await CachedAsync("hstspreload", domain, new Uri(HstsPreloadUrl + Uri.EscapeDataString(domain)), TimeSpan.FromDays(1), cancellationToken);
        return answer.Value is null ? null : SiteParsers.HstsPreloadStatus(answer.Value);
    }

    private async Task<LookupCache> CachedAsync(string kind, string key, Uri url, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var id = LookupCache.IdFor(kind, key);
        var now = time.GetUtcNow().UtcDateTime;
        using (var session = store.OpenAsyncSession())
        {
            if (await session.LoadAsync<LookupCache>(id, cancellationToken) is { ExpiresAtUtc: { } until } cached && until > now)
                return cached;
        }

        var result = await fetcher.FetchAsync(url, MaxAnswerBytes, cancellationToken, client: SeoHttp.Lookups);
        var entry = new LookupCache
        {
            Id = id,
            Kind = kind,
            Key = key.ToLowerInvariant(),
            Status = result.StatusCode,
            Value = result.Ok && !result.Truncated ? result.Text() : null,
            FetchedAtUtc = now,
            // A failure is cached briefly, so a registry that rate-limits (429) is not asked again by every run.
            ExpiresAtUtc = now + (result.Ok || result.StatusCode == 404 ? lifetime : TimeSpan.FromMinutes(15)),
        };
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(entry, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        return entry;
    }
}
