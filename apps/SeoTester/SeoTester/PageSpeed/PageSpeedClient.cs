using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Raven.Client.Documents;
using SeoTester.Crawler;
using SeoTester.Entities;

namespace SeoTester.PageSpeed;

/// <summary>PageSpeed Insights (configuration <c>SeoTester:PageSpeed</c>, D9, D13, spike S5).</summary>
public sealed class PageSpeedOptions
{
    /// <summary>
    /// The Google Cloud API key (a secret: user secrets locally, the container's environment in production; never in a
    /// file, a log or a fixture). Empty = PageSpeed Insights is "not configured" and its checks are not measured.
    /// </summary>
    public string? ApiKey { get; set; }

    public string Endpoint { get; set; } = "https://www.googleapis.com/pagespeedonline/v5/runPagespeed";

    /// <summary><c>MOBILE</c> (Google indexes mobile-first) or <c>DESKTOP</c>; PSI's own default is desktop (S5).</summary>
    public string Strategy { get; set; } = "MOBILE";

    /// <summary>How long a measured page is reused (LookupCache, §4.2): a re-run the same day spends no quota.</summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromDays(1);

    /// <summary>After a 429, the stage waits this long times the attempt before asking again ...</summary>
    public TimeSpan QuotaRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>... at most this often; then the pages are recorded "not measured: quota exhausted".</summary>
    public int QuotaRetries { get; set; } = 3;
}

/// <summary>
/// Calls PageSpeed Insights v5 for one page (S5): through the SSRF-safe client, the key in the <c>X-Goog-Api-Key</c>
/// header (never in the URL, so neither a log line nor a stored URL can hold it), a field mask that keeps the answer
/// to the parts SeoTester reads, and only the summary kept (cached a day in <c>LookupCaches/psi/…</c>).
/// </summary>
public sealed class PageSpeedClient(IDocumentStore store, PageFetcher fetcher, IOptions<PageSpeedOptions> options, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PageSpeedOptions Options => options.Value;

    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    /// <summary>The request URL for <paramref name="page"/> (no key: it travels in a header).</summary>
    public static Uri RequestUrl(PageSpeedOptions o, Uri page)
        => new($"{o.Endpoint}?url={Uri.EscapeDataString(page.AbsoluteUri)}&strategy={Uri.EscapeDataString(o.Strategy)}&category=PERFORMANCE&fields={Uri.EscapeDataString(PsiReport.FieldMask)}");

    public async Task<PsiOutcome> MeasureAsync(Uri page, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!Configured)
            return new PsiOutcome(PsiOutcomeKind.Failed, null, "not configured");
        var key = o.Strategy.ToLowerInvariant() + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(page.AbsoluteUri)))[..32];
        var id = LookupCache.IdFor("psi", key);
        var now = time.GetUtcNow().UtcDateTime;
        using (var session = store.OpenAsyncSession())
        {
            if (await session.LoadAsync<LookupCache>(id, cancellationToken) is { ExpiresAtUtc: { } until, Value: { } cached } && until > now
                && JsonSerializer.Deserialize<PsiSummary>(cached, Json) is { } summary)
                return PsiOutcome.Measured(summary);
        }

        var result = await fetcher.FetchAsync(RequestUrl(o, page), (int)(8 * 1024 * 1024), cancellationToken, client: SeoHttp.PageSpeed,
            requestHeaders: new Dictionary<string, string> { ["X-Goog-Api-Key"] = o.ApiKey! });
        if (result.StatusCode == 0)
            return new PsiOutcome(PsiOutcomeKind.Failed, null, "PageSpeed Insights did not answer");
        var outcome = PsiReport.Parse(result.StatusCode, result.Text(), page.AbsoluteUri);
        if (outcome.Summary is { } measured)
        {
            using var session = store.OpenAsyncSession();
            await session.StoreAsync(new LookupCache
            {
                Id = id,
                Kind = "psi",
                Key = key,
                Status = result.StatusCode,
                Value = JsonSerializer.Serialize(measured, Json),
                FetchedAtUtc = now,
                ExpiresAtUtc = now + o.CacheFor,
            }, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        return outcome;
    }
}
