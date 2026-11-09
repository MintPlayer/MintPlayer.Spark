using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.PlatformLifecycle;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Assertions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PlatformLifecycle;

/// <summary>
/// The platform lifecycle collector (#568 M3, S10; per channel since #606 M4), over responses captured live
/// (Fixtures/PlatformLifecycle, see its SOURCE.md). <c>npm-dist-tags-angular-core-preview.json</c> is the one derived
/// fixture: the real Angular tags with <c>next</c> set to 23.0.0-next.0, because no framework had a preview major that day.
/// </summary>
public class PlatformLifecycleTests : CoverageRavenTest
{
    private static readonly DateTime Now = new(2026, 11, 9, 4, 17, 0, DateTimeKind.Utc);
    private const string Url = "https://source.example/";

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static PlatformRelease Row(IReadOnlyList<PlatformRelease> rows, string channel) => rows.Single(r => r.Channel == channel);

    [Fact]
    public void Dotnet_reads_each_channel_with_its_phase_and_latest_sdk()
    {
        var rows = PlatformLifecycleSources.ReadDotnet(Fixture.Read("PlatformLifecycle/dotnet-releases-index.json"), Url, Now);

        rows.Select(r => r.Channel).Should().Equal("11.0", "10.0", "9.0", "8.0", "7.0", "6.0", "5.0", "3.1", "3.0", "2.2", "2.1", "2.0", "1.1", "1.0");
        Row(rows, "11.0").Should().BeEquivalentTo(new
        {
            Id = "PlatformReleases/dotnet/11.0", Major = 11, Name = "11.0", Phase = "preview", IsLts = false, SupportEndsOn = (DateTime?)null,
            LatestVersion = "11.0.0-rc.3", LatestSdk = "11.0.100-rc.3.26522.148", LatestReleasedOn = (DateTime?)Day(2026, 10, 25),
        });
        Row(rows, "10.0").Should().BeEquivalentTo(new { Phase = "active", IsLts = true, SupportEndsOn = (DateTime?)Day(2028, 11, 14), LatestSdk = "10.0.403" });
        Row(rows, "8.0").Phase.Should().Be("maintenance");
        Row(rows, "3.1").Major.Should().Be(3, "3.0 and 3.1 are channels of one major, each a row of its own");
        rows.Should().OnlyContain(r => r.ReleasedOn == null && r.Source == Url && r.CollectedAtUtc == Now);
    }

    [Fact]
    public void Endoflife_reads_the_phase_from_its_booleans()
    {
        var rows = PlatformLifecycleSources.ReadEndOfLife("angular", Fixture.Read("PlatformLifecycle/endoflife-angular.json"), Url, Now);

        Row(rows, "22").Should().BeEquivalentTo(new
        {
            Id = "PlatformReleases/angular/22", Platform = "angular", Major = 22, Phase = "active", IsLts = false, LatestVersion = "22.2.4",
            ReleasedOn = (DateTime?)Day(2026, 6, 3),
            ActiveSupportEndsOn = (DateTime?)Day(2027, 6, 30),
            SupportEndsOn = (DateTime?)Day(2028, 6, 30),
        });
        Row(rows, "9").Phase.Should().Be("eol", "isMaintained stays true for commercial support; isEol decides");
        rows.Select(r => r.Major).Should().BeInDescendingOrder();
    }

    [Theory]
    [InlineData("node", "endoflife-nodejs.json", "26", "26.13.0")]
    [InlineData("python", "endoflife-python.json", "3.15", "3.15.2")]
    [InlineData("php", "endoflife-php.json", "8.5", "8.5.12")]
    [InlineData("ruby", "endoflife-ruby.json", "4.0", "4.0.9")]
    [InlineData("rust", "endoflife-rust.json", "1.99", "1.99.1")]
    [InlineData("go", "endoflife-go.json", "1.27", "1.27.3")]
    public void Endoflife_rows_are_channels_newest_first(string platform, string fixture, string newest, string latest)
    {
        var rows = PlatformLifecycleSources.ReadEndOfLife(platform, Fixture.Read("PlatformLifecycle/" + fixture), Url, Now);

        rows[0].Should().BeEquivalentTo(new { Id = $"PlatformReleases/{platform}/{newest}", Channel = newest, LatestVersion = latest });
        rows.Select(r => r.Channel).Should().BeInDescendingOrder(PlatformLifecycleSources.ChannelComparer.Instance);
    }

    [Fact]
    public void Node_lts_channels_are_lts()
    {
        var rows = PlatformLifecycleSources.ReadEndOfLife("node", Fixture.Read("PlatformLifecycle/endoflife-nodejs.json"), Url, Now);

        Row(rows, "26").IsLts.Should().BeTrue();
        Row(rows, "25").Should().BeEquivalentTo(new { IsLts = false, Phase = "eol" });
    }

    [Theory]
    [InlineData("npm-dist-tags-angular-core.json", null)]          // next 22.3.0-next.3 is the same major as latest
    [InlineData("npm-dist-tags-nestjs-core.json", null)]           // next 12.0.0-alpha.7 was left behind latest 12.3.3
    [InlineData("npm-dist-tags-angular-core-preview.json", 23)]
    public void A_preview_major_is_a_next_above_latest(string fixture, int? major)
        => PlatformLifecycleSources.ReadPreview(Fixture.Read("PlatformLifecycle/" + fixture))?.Major.Should().Be(major!.Value);

    [Theory]
    [InlineData("node", "node-rc-index.json", "26", "26.4.0-rc.0", 2026, 6, 23, null)]
    [InlineData("python", "python-prereleases.json", "3.16", "3.16.0a1", 2026, 10, 13, null)]
    [InlineData("php", "php-release-candidates.json", "8.6", "8.6.0RC5", 2026, 11, 5, null)]
    [InlineData("ruby", "ruby-releases.yml", "4.1", "4.1.0-preview1", 2026, 10, 12, null)]
    [InlineData("rust", "rust-channel-beta.toml", "1.100", "1.100.0-beta.13", 2026, 11, 8, "beta-2026-11-08")]
    public void A_preview_feed_names_its_newest_channel(string platform, string fixture, string channel, string version, int year, int month, int day, string? name)
    {
        var previews = PlatformLifecycleSources.ReadPreviews(platform, Fixture.Read("PlatformLifecycle/" + fixture));

        previews.Should().NotBeNull();
        previews![0].Should().Be(new PlatformPreview(channel, version, Day(year, month, day), name));
        previews.Select(p => p.Channel).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_php_candidate_is_skipped_only_when_it_says_it_is_disabled()
    {
        // The capture's withdrawn 8.2/8.3 entries say "enabled": false; the published ones carry no enabled at all.
        var previews = PlatformLifecycleSources.ReadPhpCandidates(Fixture.Read("PlatformLifecycle/php-release-candidates.json"));

        previews.Select(p => p.Version).Should().Equal("8.6.0RC5", "8.5.13RC1", "8.4.28RC1");
    }

    [Fact]
    public void A_platform_without_a_preview_feed_reads_none()
        => PlatformLifecycleSources.ReadPreviews("go", "{}").Should().BeNull();

    [Theory]
    [InlineData("22", 22)]
    [InlineData("10.0", 10)]
    [InlineData("23.0.0-next.0", 23)]
    [InlineData("v22", null)]
    [InlineData("0", null)]
    [InlineData("1234567890", null)]
    public void A_major_is_the_leading_number(string text, int? major)
        => PlatformLifecycleSources.MajorOf(text).Should().Be(major);

    [Theory]
    [InlineData("24", true)]
    [InlineData("10.0", true)]
    [InlineData("1.100", true)]
    [InlineData("1.2.3", false)]
    [InlineData("v24", false)]
    [InlineData("24/../x", false)]
    public void A_channel_is_one_or_two_short_numbers(string text, bool channel)
        => PlatformLifecycleSources.IsChannel(text).Should().Be(channel);

    [Fact]
    public void Channels_order_numerically_per_segment()
        => new[] { "1.99", "1.100", "1.9", "2" }.Order(PlatformLifecycleSources.ChannelComparer.Instance).Should().Equal("1.9", "1.99", "1.100", "2");

    private static (StubHttpMessageHandler Handler, List<string> Asked) Sources(Dictionary<string, string?> bodies)
    {
        var asked = new List<string>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            asked.Add(url);
            return bodies.TryGetValue(url, out var body) && body is not null
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        return (handler, asked);
    }

    private static readonly PlatformLifecycleOptions Options = new()
    {
        DotnetIndexUrl = "https://dotnet.example/index.json",
        EndOfLifeUrl = "https://eol.example/",
        NpmDistTagsUrl = "https://npm.example/",
        Platforms = new(StringComparer.Ordinal)
        {
            ["angular"] = new() { Product = "angular", NpmPackage = "@angular/core" },
            ["node"] = new() { Product = "nodejs", PreviewUrl = "https://rc.example/node" },
            ["rust"] = new() { Product = "rust", PreviewUrl = "https://rc.example/rust" },
        },
    };

    private static async Task RunAsync(IDocumentStore store, StubHttpMessageHandler handler, PlatformLifecycleOptions? options = null)
    {
        using var session = store.OpenAsyncSession();
        await new PlatformLifecycleCronJob(session, new SingleClientHttpFactory(handler), Microsoft.Extensions.Options.Options.Create(options ?? Options),
            new FixedTimeProvider { UtcNow = Now }, NullLogger<PlatformLifecycleCronJob>.Instance).RunAsync(CancellationToken.None);
    }

    /// <summary>
    /// #606 S1: the lookup's table, built from every real capture through the collector, is one <c>workflow_dispatch</c>
    /// input, which GitHub caps at 65,535 characters. The SDKs only: the frameworks are not in it.
    /// </summary>
    [Fact]
    public async Task The_lookup_table_from_every_real_source_fits_one_dispatch_input()
    {
        using var store = GetDocumentStore();
        var options = new PlatformLifecycleOptions
        {
            DotnetIndexUrl = "https://dotnet.example/index.json",
            EndOfLifeUrl = "https://eol.example/",
            Platforms = new(new PlatformLifecycleOptions().Platforms.Where(p => p.Key is not ("angular" or "react" or "vue"))
                .ToDictionary(p => p.Key, p => new PlatformLifecycleSource { Product = p.Value.Product, PreviewUrl = p.Value.PreviewUrl is null ? null : $"https://rc.example/{p.Key}" }),
                StringComparer.Ordinal),
        };
        var bodies = new Dictionary<string, string?> { ["https://dotnet.example/index.json"] = Fixture.Read("PlatformLifecycle/dotnet-releases-index.json") };
        foreach (var (sdk, product, preview) in new[]
        {
            ("node", "nodejs", "node-rc-index.json"), ("python", "python", "python-prereleases.json"), ("java", "eclipse-temurin", null),
            ("go", "go", null), ("php", "php", "php-release-candidates.json"), ("ruby", "ruby", "ruby-releases.yml"), ("rust", "rust", "rust-channel-beta.toml"),
        })
        {
            bodies[$"https://eol.example/{product}"] = Fixture.Read($"PlatformLifecycle/endoflife-{product}.json");
            if (preview is not null) bodies[$"https://rc.example/{sdk}"] = Fixture.Read("PlatformLifecycle/" + preview);
        }
        await RunAsync(store, Sources(bodies).Handler, options);

        using var session = store.OpenAsyncSession();
        var table = await CodeCoverage.Dependencies.Policy.DependencyPlatformTable.JsonAsync(session, ["@types/node"], CancellationToken.None);

        table.Length.Should().BeLessThan(32_768, "half the input's 65,535 cap, so the channels can double before it matters");
        using var document = System.Text.Json.JsonDocument.Parse(table);
        var sdks = document.RootElement.GetProperty("sdks");
        sdks.EnumerateObject().Select(p => p.Name).Should().Equal("dotnet", "go", "java", "node", "php", "python", "ruby", "rust");
        var rust = sdks.GetProperty("rust").EnumerateArray().Single(c => c.GetProperty("channel").GetString() == "1.100");
        (rust.GetProperty("preview").GetBoolean(), rust.GetProperty("latestSdk").GetString()).Should().Be((true, "beta-2026-11-08"));
        var dotnet = sdks.GetProperty("dotnet").EnumerateArray().Single(c => c.GetProperty("channel").GetString() == "10.0");
        (dotnet.GetProperty("lts").GetBoolean(), dotnet.GetProperty("latestSdk").GetString(), dotnet.GetProperty("eol").GetString()).Should().Be((true, "10.0.403", "2028-11-14"));
        sdks.GetProperty("go").EnumerateArray().Should().OnlyContain(c => c.GetProperty("lts").ValueKind == System.Text.Json.JsonValueKind.Null, "go names no LTS release");
        sdks.GetProperty("dotnet").EnumerateArray().Should().NotContain(c => c.GetProperty("channel").GetString() == "3.1", "an end-of-life channel is never offered");
        document.RootElement.GetProperty("unstable").EnumerateArray().Select(u => u.GetString()).Should().Equal("@types/node");
    }

    private static Dictionary<string, string?> AllSources() => new()
    {
        ["https://dotnet.example/index.json"] = Fixture.Read("PlatformLifecycle/dotnet-releases-index.json"),
        ["https://eol.example/angular"] = Fixture.Read("PlatformLifecycle/endoflife-angular.json"),
        // npm's real dist-tags path (registry.npmjs.org/-/package/@angular/core alone answers 404, verified 2026-11-09).
        ["https://npm.example/@angular/core/dist-tags"] = Fixture.Read("PlatformLifecycle/npm-dist-tags-angular-core-preview.json"),
        ["https://eol.example/nodejs"] = Fixture.Read("PlatformLifecycle/endoflife-nodejs.json"),
        ["https://rc.example/node"] = Fixture.Read("PlatformLifecycle/node-rc-index.json"),
        ["https://eol.example/rust"] = Fixture.Read("PlatformLifecycle/endoflife-rust.json"),
        ["https://rc.example/rust"] = Fixture.Read("PlatformLifecycle/rust-channel-beta.toml"),
    };

    [Fact]
    public async Task A_run_stores_every_channel_and_the_preview_channels()
    {
        using var store = GetDocumentStore();
        var (handler, asked) = Sources(AllSources());

        await RunAsync(store, handler);

        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/angular/23")).Should().BeEquivalentTo(new
        {
            Platform = "angular", Channel = "23", Major = 23, Phase = "preview", LatestVersion = "23.0.0-next.0", Source = "https://npm.example/@angular/core/dist-tags",
        });
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/rust/1.100")).Should().BeEquivalentTo(new
        {
            Channel = "1.100", Name = "beta-2026-11-08", Phase = "preview", LatestVersion = "1.100.0-beta.13", LatestReleasedOn = (DateTime?)Day(2026, 11, 8),
        });
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/dotnet/10.0")).Phase.Should().Be("active");
        // 26's release candidates are older than its release: node has no preview channel in the capture.
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/node/26")).Should().BeEquivalentTo(new { IsLts = true, Phase = "active" });
        (await session.Advanced.LoadStartingWithAsync<PlatformRelease>("PlatformReleases/node/27")).Should().BeEmpty();
        asked.Should().NotContain(u => u.StartsWith("https://npm.example/", StringComparison.Ordinal) && !u.EndsWith("@angular/core/dist-tags", StringComparison.Ordinal),
            "only angular names an npm package");
    }

    [Fact]
    public async Task A_run_deletes_the_rows_its_answered_sources_no_longer_list()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            // A per-major id from before #606, a channel the index no longer lists, and a platform no longer collected.
            await seed.StoreAsync(new PlatformRelease { Platform = "dotnet", Channel = "10", Major = 10, Name = "10.0", Phase = "active" }, "PlatformReleases/dotnet/10");
            await seed.StoreAsync(new PlatformRelease { Platform = "dotnet", Channel = "12.0", Major = 12, Name = "12.0", Phase = "preview" }, "PlatformReleases/dotnet/12.0");
            await seed.StoreAsync(new PlatformRelease { Platform = "nodejs", Channel = "26", Major = 26, Name = "26", Phase = "active" }, "PlatformReleases/nodejs/26");
            await seed.SaveChangesAsync();
        }
        var (handler, _) = Sources(AllSources());

        await RunAsync(store, handler);

        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<PlatformRelease>(["PlatformReleases/dotnet/10", "PlatformReleases/dotnet/12.0", "PlatformReleases/nodejs/26"]))
            .Values.Should().OnlyContain(r => r == null);
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/dotnet/10.0")).Should().NotBeNull();
    }

    [Fact]
    public async Task A_source_that_fails_leaves_its_rows_as_they_were_and_the_others_are_updated()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new PlatformRelease { Platform = "angular", Channel = "22", Major = 22, Name = "22", Phase = "active", Source = "yesterday" }, "PlatformReleases/angular/22");
            await seed.StoreAsync(new PlatformRelease { Platform = "dotnet", Channel = "10.0", Major = 10, Name = "10.0", Phase = "preview", Source = "yesterday" }, "PlatformReleases/dotnet/10.0");
            await seed.SaveChangesAsync();
        }
        var (handler, _) = Sources(new()
        {
            ["https://dotnet.example/index.json"] = Fixture.Read("PlatformLifecycle/dotnet-releases-index.json"),
            ["https://eol.example/angular"] = null,
            ["https://eol.example/nodejs"] = "{ \"not\": \"the shape\" }",
        });

        await RunAsync(store, handler);

        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/angular/22")).Source.Should().Be("yesterday");
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/dotnet/10.0")).Should().BeEquivalentTo(new { Phase = "active", Source = "https://dotnet.example/index.json" });
        (await session.Advanced.LoadStartingWithAsync<PlatformRelease>("PlatformReleases/node/")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_preview_feed_that_fails_keeps_the_released_channels()
    {
        using var store = GetDocumentStore();
        var sources = AllSources();
        sources["https://rc.example/rust"] = null;
        var (handler, _) = Sources(sources);

        await RunAsync(store, handler);

        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/rust/1.99")).Should().NotBeNull();
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/rust/1.100")).Should().BeNull();
    }

    [Fact]
    public async Task A_source_that_does_not_answer_is_skipped_and_the_others_are_stored()
    {
        using var store = GetDocumentStore();
        var handler = new StubHttpMessageHandler((request, _) => request.RequestUri!.Host == "dotnet.example"
            ? throw new HttpRequestException("connection refused")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/angular", StringComparison.Ordinal)
                    ? Fixture.Read("PlatformLifecycle/endoflife-angular.json")
                    : "{}"),
            });

        await RunAsync(store, handler);

        using var session = store.OpenAsyncSession();
        (await session.Advanced.LoadStartingWithAsync<PlatformRelease>("PlatformReleases/dotnet/")).Should().BeEmpty();
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/angular/22")).Phase.Should().Be("active");
    }
}
