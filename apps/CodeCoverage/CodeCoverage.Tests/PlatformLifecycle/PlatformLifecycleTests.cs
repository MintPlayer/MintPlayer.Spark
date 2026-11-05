using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.PlatformLifecycle;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Assertions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PlatformLifecycle;

/// <summary>
/// The platform lifecycle collector (#568 M3, S10), over responses captured live on 2026-11-05 (Fixtures/PlatformLifecycle).
/// <c>npm-dist-tags-angular-core-preview.json</c> is the one derived fixture: the real Angular tags with <c>next</c> set to
/// 23.0.0-next.0, because no framework had a preview major on that day.
/// </summary>
public class PlatformLifecycleTests : CoverageRavenTest
{
    private static readonly DateTime Now = new(2026, 11, 5, 4, 17, 0, DateTimeKind.Utc);
    private const string Url = "https://source.example/";

    private static PlatformRelease Row(IReadOnlyList<PlatformRelease> rows, int major) => rows.Single(r => r.Major == major);

    [Fact]
    public void Dotnet_reads_each_major_once_with_its_phase()
    {
        var rows = PlatformLifecycleSources.ReadDotnet(Fixture.Read("PlatformLifecycle/dotnet-releases-index.json"), Url, Now);

        rows.Select(r => r.Major).Should().Equal(11, 10, 9, 8, 7, 6, 5, 3, 2, 1);
        Row(rows, 11).Should().BeEquivalentTo(new { Id = "PlatformReleases/dotnet/11", Name = "11.0", Phase = "preview", IsLts = false, SupportEndsOn = (DateTime?)null, LatestVersion = "11.0.0-rc.3" });
        Row(rows, 10).Should().BeEquivalentTo(new { Phase = "active", IsLts = true, SupportEndsOn = (DateTime?)new DateTime(2028, 11, 14, 0, 0, 0, DateTimeKind.Utc) });
        Row(rows, 8).Phase.Should().Be("maintenance");
        Row(rows, 3).Name.Should().Be("3.1", "the highest channel of a major stands for it");
        Row(rows, 2).Name.Should().Be("2.2");
        rows.Should().OnlyContain(r => r.ReleasedOn == null && r.Source == Url && r.CollectedAtUtc == Now);
    }

    [Fact]
    public void Endoflife_reads_the_phase_from_its_booleans()
    {
        var rows = PlatformLifecycleSources.ReadEndOfLife("angular", Fixture.Read("PlatformLifecycle/endoflife-angular.json"), Url, Now);

        Row(rows, 22).Should().BeEquivalentTo(new
        {
            Id = "PlatformReleases/angular/22", Platform = "angular", Phase = "active", IsLts = false, LatestVersion = "22.2.4",
            ReleasedOn = (DateTime?)new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc),
            ActiveSupportEndsOn = (DateTime?)new DateTime(2027, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            SupportEndsOn = (DateTime?)new DateTime(2028, 6, 30, 0, 0, 0, DateTimeKind.Utc),
        });
        Row(rows, 9).Phase.Should().Be("eol", "isMaintained stays true for commercial support; isEol decides");
        rows.Select(r => r.Major).Should().BeInDescendingOrder();
    }

    [Fact]
    public void Node_lts_majors_are_lts()
    {
        var rows = PlatformLifecycleSources.ReadEndOfLife("nodejs", Fixture.Read("PlatformLifecycle/endoflife-nodejs.json"), Url, Now);

        Row(rows, 26).IsLts.Should().BeTrue();
        Row(rows, 1).Phase.Should().Be("eol");
    }

    [Theory]
    [InlineData("npm-dist-tags-angular-core.json", null)]          // next 22.3.0-next.3 is the same major as latest
    [InlineData("npm-dist-tags-nestjs-core.json", null)]           // next 12.0.0-alpha.7 was left behind latest 12.3.3
    [InlineData("npm-dist-tags-angular-core-preview.json", 23)]
    public void A_preview_major_is_a_next_above_latest(string fixture, int? major)
        => PlatformLifecycleSources.ReadPreview(Fixture.Read("PlatformLifecycle/" + fixture))?.Major.Should().Be(major!.Value);

    [Theory]
    [InlineData("22", 22)]
    [InlineData("10.0", 10)]
    [InlineData("23.0.0-next.0", 23)]
    [InlineData("v22", null)]
    [InlineData("0", null)]
    [InlineData("1234567890", null)]
    public void A_major_is_the_leading_number(string text, int? major)
        => PlatformLifecycleSources.MajorOf(text).Should().Be(major);

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
        Products = new() { ["angular"] = "@angular/core", ["nodejs"] = "" },
    };

    private static async Task RunAsync(IDocumentStore store, StubHttpMessageHandler handler)
    {
        using var session = store.OpenAsyncSession();
        await new PlatformLifecycleCronJob(session, new SingleClientHttpFactory(handler), Microsoft.Extensions.Options.Options.Create(Options),
            new FixedTimeProvider { UtcNow = Now }, NullLogger<PlatformLifecycleCronJob>.Instance).RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_run_stores_every_platform_and_a_framework_preview_major()
    {
        using var store = GetDocumentStore();
        var (handler, asked) = Sources(new()
        {
            ["https://dotnet.example/index.json"] = Fixture.Read("PlatformLifecycle/dotnet-releases-index.json"),
            ["https://eol.example/angular"] = Fixture.Read("PlatformLifecycle/endoflife-angular.json"),
            ["https://npm.example/@angular/core"] = Fixture.Read("PlatformLifecycle/npm-dist-tags-angular-core-preview.json"),
            ["https://eol.example/nodejs"] = Fixture.Read("PlatformLifecycle/endoflife-nodejs.json"),
        });

        await RunAsync(store, handler);

        using var session = store.OpenAsyncSession();
        var preview = await session.LoadAsync<PlatformRelease>("PlatformReleases/angular/23");
        preview.Should().BeEquivalentTo(new { Platform = "angular", Major = 23, Phase = "preview", LatestVersion = "23.0.0-next.0", Source = "https://npm.example/@angular/core" });
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/dotnet/10")).Phase.Should().Be("active");
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/nodejs/26")).IsLts.Should().BeTrue();
        asked.Should().NotContain(u => u.StartsWith("https://npm.example/", StringComparison.Ordinal) && !u.EndsWith("@angular/core", StringComparison.Ordinal), "Node.js names no npm package");
    }

    [Fact]
    public async Task A_source_that_fails_leaves_its_rows_as_they_were_and_the_others_are_updated()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new PlatformRelease { Platform = "angular", Major = 22, Name = "22", Phase = "active", Source = "yesterday" }, "PlatformReleases/angular/22");
            await seed.StoreAsync(new PlatformRelease { Platform = "dotnet", Major = 10, Name = "10.0", Phase = "preview", Source = "yesterday" }, "PlatformReleases/dotnet/10");
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
        (await session.LoadAsync<PlatformRelease>("PlatformReleases/dotnet/10")).Should().BeEquivalentTo(new { Phase = "active", Source = "https://dotnet.example/index.json" });
        (await session.Advanced.LoadStartingWithAsync<PlatformRelease>("PlatformReleases/nodejs/")).Should().BeEmpty();
    }
}
