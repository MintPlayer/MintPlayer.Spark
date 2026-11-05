using CodeCoverage.Entities;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PlatformLifecycle;

/// <summary>Where the platform lifecycle comes from (#568 S10). Configuration section <c>PlatformLifecycle</c>.</summary>
public sealed class PlatformLifecycleOptions
{
    public const string Section = "PlatformLifecycle";
    public const string HttpClientName = "PlatformLifecycle";

    public string DotnetIndexUrl { get; set; } = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    /// <summary>endoflife.date's v1 product endpoint; the product name is appended.</summary>
    public string EndOfLifeUrl { get; set; } = "https://endoflife.date/api/v1/products/";

    /// <summary>The npm registry's dist-tags endpoint; the package name is appended.</summary>
    public string NpmDistTagsUrl { get; set; } = "https://registry.npmjs.org/-/package/";

    /// <summary>
    /// The endoflife.date products to collect besides .NET, each with the npm package whose <c>next</c> dist-tag names its
    /// preview major (empty for none). Configuration adds products to these.
    /// </summary>
    public Dictionary<string, string> Products { get; set; } = new(StringComparer.Ordinal)
    {
        ["angular"] = "@angular/core",
        ["react"] = "react",
        ["vue"] = "vue",
        ["nodejs"] = "",
    };
}

/// <summary>
/// Collects the lifecycle of every platform major once a day (#568 M3) into <see cref="PlatformRelease"/> documents, the
/// public list of supported platforms. A source that fails is logged and skipped: its rows keep yesterday's facts rather
/// than vanish. The sources ask for no rate limit, and the .NET index allows 15 minutes of caching, so daily is plenty.
/// </summary>
public partial class PlatformLifecycleCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IOptions<PlatformLifecycleOptions> options;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<PlatformLifecycleCronJob> logger;

    public static string CronSchedule => "17 4 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var http = httpClientFactory.CreateClient(PlatformLifecycleOptions.HttpClientName);
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = new List<PlatformRelease>();

        if (await FetchAsync(http, settings.DotnetIndexUrl, cancellationToken) is { } dotnet)
            rows.AddRange(Read(() => PlatformLifecycleSources.ReadDotnet(dotnet, settings.DotnetIndexUrl, now), settings.DotnetIndexUrl));

        foreach (var (product, npmPackage) in settings.Products.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // The product is configuration, but it lands in a URL and a document id: only a plain name is sent anywhere.
            if (!Ecosystems.IsName(product)) { logger.LogWarning("Platform lifecycle: '{Product}' is not a product name; skipped", product); continue; }
            var url = settings.EndOfLifeUrl + product;
            if (await FetchAsync(http, url, cancellationToken) is not { } json) continue;
            var releases = Read(() => PlatformLifecycleSources.ReadEndOfLife(product, json, url, now), url);
            rows.AddRange(releases);

            if (string.IsNullOrEmpty(npmPackage) || releases.Count == 0) continue;
            var tagsUrl = settings.NpmDistTagsUrl + npmPackage;
            if (await FetchAsync(http, tagsUrl, cancellationToken) is { } tags
                && Read<(int Major, string Version)>(() => PlatformLifecycleSources.ReadPreview(tags) is { } p ? [p] : [], tagsUrl) is [var (major, version)]
                && releases.All(r => r.Major != major))
            {
                rows.Add(new PlatformRelease
                {
                    Id = PlatformRelease.IdFor(product, major), Platform = product, Major = major, Name = major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Phase = "preview", LatestVersion = version, Source = tagsUrl, CollectedAtUtc = now,
                });
            }
        }

        if (rows.Count == 0) return;
        var stored = await session.LoadAsync<PlatformRelease>(rows.Select(r => r.Id!).Distinct(), cancellationToken);
        foreach (var row in rows)
        {
            if (stored.GetValueOrDefault(row.Id!) is { } existing) Copy(row, existing);
            else await session.StoreAsync(row, row.Id, cancellationToken);
        }
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Platform lifecycle: {Count} platform majors collected", rows.Count);
    }

    private IReadOnlyList<T> Read<T>(Func<IReadOnlyList<T>> read, string url)
    {
        try { return read(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Platform lifecycle: {Url} answered in an unexpected shape; skipped", url);
            return [];
        }
    }

    private async Task<string?> FetchAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Platform lifecycle: {Url} answered {Status}; skipped", url, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Platform lifecycle: {Url} did not answer; skipped", url);
        }
        return null;
    }

    private static void Copy(PlatformRelease from, PlatformRelease to)
    {
        to.Platform = from.Platform;
        to.Major = from.Major;
        to.Name = from.Name;
        to.Phase = from.Phase;
        to.IsLts = from.IsLts;
        to.ReleasedOn = from.ReleasedOn;
        to.ActiveSupportEndsOn = from.ActiveSupportEndsOn;
        to.SupportEndsOn = from.SupportEndsOn;
        to.LatestVersion = from.LatestVersion;
        to.Source = from.Source;
        to.CollectedAtUtc = from.CollectedAtUtc;
    }
}
