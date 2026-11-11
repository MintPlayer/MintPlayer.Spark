using CodeCoverage.Entities;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PlatformLifecycle;

/// <summary>One platform the collector reads besides .NET (#606 D6): its endoflife.date product, and where its preview
/// channel comes from (an npm package's <c>next</c> dist-tag for a framework, a release-candidate feed for an SDK).</summary>
public sealed class PlatformLifecycleSource
{
    /// <summary>The endoflife.date product: <c>nodejs</c> for node, <c>eclipse-temurin</c> for java.</summary>
    public string Product { get; set; } = string.Empty;

    /// <summary>A framework's npm package, whose <c>next</c> dist-tag names its preview major.</summary>
    public string? NpmPackage { get; set; }

    /// <summary>An SDK's preview feed, read by <see cref="PlatformLifecycleSources.ReadPreviews"/>.</summary>
    public string? PreviewUrl { get; set; }
}

/// <summary>Where the platform lifecycle comes from (#568 S10, #606 D6). Configuration section <c>PlatformLifecycle</c>.</summary>
public sealed class PlatformLifecycleOptions
{
    public const string Section = "PlatformLifecycle";
    public const string HttpClientName = "PlatformLifecycle";

    public string DotnetIndexUrl { get; set; } = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    /// <summary>endoflife.date's v1 product endpoint; the product name is appended.</summary>
    public string EndOfLifeUrl { get; set; } = "https://endoflife.date/api/v1/products/";

    /// <summary>The npm registry's package endpoint; <c>&lt;package&gt;/dist-tags</c> is appended. (Until #606 the
    /// <c>/dist-tags</c> was missing and npm answered 404 for every package, so no preview major was ever collected.)</summary>
    public string NpmDistTagsUrl { get; set; } = "https://registry.npmjs.org/-/package/";

    /// <summary>
    /// The platforms to collect besides .NET, by the name the rows carry. Configuration adds platforms to these. The
    /// preview feeds were picked in #606 S6 (2026-11-09); endoflife.date lists released channels only.
    /// </summary>
    public Dictionary<string, PlatformLifecycleSource> Platforms { get; set; } = new(StringComparer.Ordinal)
    {
        ["angular"] = new() { Product = "angular", NpmPackage = "@angular/core" },
        ["react"] = new() { Product = "react", NpmPackage = "react" },
        ["vue"] = new() { Product = "vue", NpmPackage = "vue" },
        ["node"] = new() { Product = "nodejs", PreviewUrl = "https://nodejs.org/download/rc/index.json" },
        ["python"] = new() { Product = "python", PreviewUrl = "https://www.python.org/api/v2/downloads/release/?pre_release=true&is_published=true" },
        ["java"] = new() { Product = "eclipse-temurin" },
        ["go"] = new() { Product = "go" },
        ["php"] = new() { Product = "php", PreviewUrl = "https://www.php.net/pre-release-builds.php?format=json" },
        ["ruby"] = new() { Product = "ruby", PreviewUrl = "https://raw.githubusercontent.com/ruby/www.ruby-lang.org/master/_data/releases.yml" },
        ["rust"] = new() { Product = "rust", PreviewUrl = "https://static.rust-lang.org/dist/channel-rust-beta.toml" },
    };
}

/// <summary>
/// Collects the lifecycle of every platform channel once a day (#568 M3, #606 M4) into <see cref="PlatformRelease"/>
/// documents: the public list of supported platforms, and the channel table every dependency lookup is given (#606 D9).
/// A source that fails is logged and skipped: its rows keep yesterday's facts rather than vanish. When a platform's
/// source answered, its rows that it no longer lists are deleted, and so are the rows of a platform no longer collected
/// (the per-major ids and the <c>nodejs</c> name before #606). The sources ask for no rate limit, and the .NET index
/// allows 15 minutes of caching, so daily is plenty.
/// </summary>
public partial class PlatformLifecycleCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IOptions<PlatformLifecycleOptions> options;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<PlatformLifecycleCronJob> logger;

    public static string CronSchedule => "17 4 * * *";

    /// <summary>An endoflife.date product slug: lowercase words joined by single hyphens.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex ProductSlug();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var http = httpClientFactory.CreateClient(PlatformLifecycleOptions.HttpClientName);
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = new List<PlatformRelease>();
        var answered = new HashSet<string>(StringComparer.Ordinal);

        if (await FetchAsync(http, settings.DotnetIndexUrl, cancellationToken) is { } dotnet
            && Read(() => PlatformLifecycleSources.ReadDotnet(dotnet, settings.DotnetIndexUrl, now), settings.DotnetIndexUrl) is { Count: > 0 } dotnetRows)
        {
            rows.AddRange(dotnetRows);
            answered.Add(PlatformLifecycleSources.Dotnet);
        }

        foreach (var (platform, source) in settings.Platforms.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // The names are configuration, but they land in URLs and document ids: only plain names are sent anywhere. The
            // platform goes into ids (a plain name); the product only into the URL, and endoflife.date slugs have hyphens
            // (`eclipse-temurin`, which Ecosystems.IsName refused, so java was never collected).
            if (!Ecosystems.IsName(platform) || !ProductSlug().IsMatch(source.Product))
            {
                logger.LogWarning("Platform lifecycle: '{Platform}' ('{Product}') is not a product name; skipped", platform, source.Product);
                continue;
            }
            var url = settings.EndOfLifeUrl + source.Product;
            if (await FetchAsync(http, url, cancellationToken) is not { } json) continue;
            var releases = Read(() => PlatformLifecycleSources.ReadEndOfLife(platform, json, url, now), url);
            if (releases.Count == 0) continue;
            rows.AddRange(releases);
            answered.Add(platform);

            if (!string.IsNullOrEmpty(source.NpmPackage))
                await AddFrameworkPreviewAsync(http, settings, platform, source.NpmPackage, releases, rows, now, cancellationToken);
            if (!string.IsNullOrEmpty(source.PreviewUrl))
                await AddSdkPreviewAsync(http, platform, source.PreviewUrl, releases, rows, now, cancellationToken);
        }

        var stored = await session.Advanced.LoadStartingWithAsync<PlatformRelease>(PlatformRelease.Prefix, pageSize: 1024, token: cancellationToken);
        var byId = stored.ToDictionary(r => r.Id!, StringComparer.Ordinal);
        var today = rows.Select(r => r.Id!).ToHashSet(StringComparer.Ordinal);
        var collected = settings.Platforms.Keys.Append(PlatformLifecycleSources.Dotnet).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var row in stored)
        {
            if (today.Contains(row.Id!)) continue;
            if (collected.Contains(row.Platform) && !answered.Contains(row.Platform)) continue;
            session.Delete(row);
            removed++;
        }
        foreach (var row in rows)
        {
            if (byId.GetValueOrDefault(row.Id!) is { } existing) Copy(row, existing);
            else await session.StoreAsync(row, row.Id, cancellationToken);
        }
        if (rows.Count == 0 && removed == 0) return;
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Platform lifecycle: {Count} platform channels collected, {Removed} stale ones removed", rows.Count, removed);
    }

    private async Task AddFrameworkPreviewAsync(HttpClient http, PlatformLifecycleOptions settings, string platform, string npmPackage,
        IReadOnlyList<PlatformRelease> releases, List<PlatformRelease> rows, DateTime now, CancellationToken cancellationToken)
    {
        var tagsUrl = $"{settings.NpmDistTagsUrl}{npmPackage}/dist-tags";
        if (await FetchAsync(http, tagsUrl, cancellationToken) is { } tags
            && Read<(int Major, string Version)>(() => PlatformLifecycleSources.ReadPreview(tags) is { } p ? [p] : [], tagsUrl) is [var (major, version)]
            && releases.All(r => r.Major != major))
        {
            var channel = major.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(new PlatformRelease
            {
                Id = PlatformRelease.IdFor(platform, channel), Platform = platform, Channel = channel, Major = major, Name = channel,
                Phase = "preview", LatestVersion = version, Source = tagsUrl, CollectedAtUtc = now,
            });
        }
    }

    /// <summary>An SDK's preview channel (#606 S6): the feed's newest channel above every released one, if any.</summary>
    private async Task AddSdkPreviewAsync(HttpClient http, string platform, string url, IReadOnlyList<PlatformRelease> releases, List<PlatformRelease> rows,
        DateTime now, CancellationToken cancellationToken)
    {
        if (await FetchAsync(http, url, cancellationToken) is not { } text) return;
        var newest = releases.Select(r => r.Channel).Max(PlatformLifecycleSources.ChannelComparer.Instance);
        var preview = Read(() => PlatformLifecycleSources.ReadPreviews(platform, text) ?? [], url)
            .FirstOrDefault(p => PlatformLifecycleSources.ChannelComparer.Instance.Compare(p.Channel, newest) > 0);
        if (preview is null) return;
        rows.Add(new PlatformRelease
        {
            Id = PlatformRelease.IdFor(platform, preview.Channel), Platform = platform, Channel = preview.Channel,
            Major = PlatformLifecycleSources.MajorOf(preview.Channel) ?? 0, Name = preview.Name ?? preview.Channel,
            Phase = "preview", LatestVersion = preview.Version, LatestReleasedOn = preview.ReleasedOn, Source = url, CollectedAtUtc = now,
        });
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
        to.Channel = from.Channel;
        to.Major = from.Major;
        to.Name = from.Name;
        to.Phase = from.Phase;
        to.IsLts = from.IsLts;
        to.ReleasedOn = from.ReleasedOn;
        to.ActiveSupportEndsOn = from.ActiveSupportEndsOn;
        to.SupportEndsOn = from.SupportEndsOn;
        to.LatestVersion = from.LatestVersion;
        to.LatestReleasedOn = from.LatestReleasedOn;
        to.LatestSdk = from.LatestSdk;
        to.Source = from.Source;
        to.CollectedAtUtc = from.CollectedAtUtc;
    }
}
