using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Settings;
using SeoTester.Sites;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Fetcher;

/// <summary>
/// The app with <c>SeoTester:Fetcher:Mode = Remote</c> (<c>docs/seo_analyzer_PRD.md</c> D54): every request to a
/// target leaves through the fetcher's API, here an in-test fetcher host whose web, DNS and TLS are the fixture's fakes.
/// The app's own fakes stay empty, which is the point: the process holding the database asks the internet nothing.
/// </summary>
[Collection(SeoRemoteFetcherHostCollection.Name)]
public class RemoteFetcherPipelineTests(SeoRemoteFetcherHostFixture host)
{
    [Fact]
    public async Task An_audit_through_the_remote_fetcher_probes_crawls_and_scores_with_nothing_fetched_by_the_app()
    {
        var h = FakeSite.NewHost();
        var o = "https://" + h;
        host.Web.Page(o + "/robots.txt", "User-agent: *\nDisallow: /private\n");
        host.Web.Html(o + "/", FakeSite.Page("Home of the example shop", "/a", "/b", "/old", "/private/secret"),
            new Dictionary<string, string> { ["Strict-Transport-Security"] = "max-age=63072000" });
        host.Web.Html(o + "/a", FakeSite.Page("Same title on two pages", "/"));
        host.Web.Html(o + "/b", FakeSite.Page("Same title on two pages", "/"));
        host.Web.Redirect(o + "/old", o + "/a", HttpStatusCode.MovedPermanently);
        host.Web.Html(o + "/private/secret", FakeSite.Page("Must never be fetched"));
        FakeSite.Registries(host.Web);
        host.Tls.Hosts[h] = FakeTls.Good(DateTime.UtcNow.AddDays(5));
        var site = await SeedSiteAsync(host, Acme, hostName: h);

        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);
        status.Should().Be(HttpStatusCode.OK, raw);
        var run = await FakeSite.FinishedAsync(host.Database, body.GetProperty("result").GetProperty("runId").GetString()!);

        run.Status.Should().Be(AuditRunStatus.Completed, run.Error);
        run.PagesDone.Should().Be(4, "/, /a, /b, /old: /private is disallowed");
        var results = run.CheckResults.ToDictionary(r => r.CheckId);
        results["seo.duplicate-titles"].SampleUrls.Should().BeEquivalentTo([o + "/a", o + "/b"]);
        // The redirect trace crossed the API.
        results["technical.redirect-chains"].SampleUrls.Should().Equal(o + "/old");
        results["security.certificate-expiry"].Status.Should().Be("Fail", "the fetcher's TLS probe saw a certificate ending in 5 days");
        results["domain.expiry"].Status.Should().Be("Pass", "RDAP through the fetcher: the recorded .com answer expires in 2028");

        var requests = host.Web.RequestsTo(h).ToList();
        requests.Should().OnlyContain(r => r.UserAgent == SeoTesterBot.UserAgent);
        requests.First().Url.Should().EndWith("/robots.txt");
        requests.Select(r => new Uri(r.Url).AbsolutePath).Should().NotContain("/private/secret", "robots.txt is still the app's to apply");
        host.AppWeb.Requests.Should().BeEmpty("the app made no request to any target itself");
        host.AppTls.Probed.Should().BeEmpty();
        host.AppDns.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task Verification_reads_dns_and_the_well_known_file_through_the_remote_fetcher()
    {
        var byDns = await SeedSiteAsync(host, Acme);
        host.Dns.Txt[OwnershipProof.RecordName(byDns.Host)] = [OwnershipProof.ExpectedValue(byDns.VerificationToken!)];
        var byFile = await SeedSiteAsync(host, Acme);
        host.Web.Page(byFile.Origin + OwnershipProof.WellKnownPath, OwnershipProof.ExpectedValue(byFile.VerificationToken!) + "\n");
        var redirected = await SeedSiteAsync(host, Acme);
        host.Web.Redirect(redirected.Origin + "/", "https://attacker.example.org/");
        host.Web.Page("https://attacker.example.org/", $"<html><head>{OwnershipProof.MetaTag(redirected.VerificationToken!)}</head></html>");

        (await ActionAsync(host, SiteTypeId, "Site", "VerifySite", byDns.Id!, Ann)).Body.GetProperty("result").GetProperty("outcome").GetString().Should().Be("Verified");
        (await ActionAsync(host, SiteTypeId, "Site", "VerifySite", byFile.Id!, Ann)).Body.GetProperty("result").GetProperty("outcome").GetString().Should().Be("Verified");
        (await ActionAsync(host, SiteTypeId, "Site", "VerifySite", redirected.Id!, Ann)).Body.GetProperty("result").GetProperty("outcome").GetString().Should().Be("NoProof");

        host.Web.RequestsTo("attacker.example.org").Should().BeEmpty("the fetcher's verification client refuses a redirect off the origin");
        host.AppWeb.Requests.Should().BeEmpty();
        host.AppDns.Asked.Should().BeEmpty();
    }
}

/// <summary>
/// The app in Remote mode (D54) over an in-test fetcher (<see cref="FetcherTestHost"/>): <see cref="SeoTesterHostFixture.Web"/>,
/// <see cref="SeoTesterHostFixture.Dns"/> and <see cref="SeoTesterHostFixture.Tls"/> are the fetcher's; the app gets
/// fakes of its own (<see cref="AppWeb"/>, …) that must stay untouched.
/// </summary>
public sealed class SeoRemoteFetcherHostFixture : SeoTesterHostFixture
{
    public FakeWeb AppWeb { get; } = new();

    public RecordingDns AppDns { get; } = new();

    public RecordingTls AppTls { get; } = new();

    public FetcherTestHost Fetcher { get; private set; } = null!;

    protected override IReadOnlyDictionary<string, string?>? HostSettings => new Dictionary<string, string?>
    {
        ["SeoTester:Limits:Unverified:RequestInterval"] = "00:00:00.020",
        ["SeoTester:Crawl:BatchSize"] = "4",
        ["SeoTester:Fetcher:Mode"] = "Remote",
        ["SeoTester:Fetcher:Url"] = FetcherTestHost.BaseAddress.AbsoluteUri,
        ["SeoTester:Fetcher:Key"] = FetcherTestHost.Key,
    };

    public override async Task InitializeAsync()
    {
        Fetcher = await FetcherTestHost.StartAsync(Web, Dns, Tls);
        await base.InitializeAsync();
    }

    protected override SeoTesterWebAppFactory CreateFactory() => new(Store, HostSettings, dns: null, web: AppWeb, tls: null,
        configureServices: services =>
        {
            services.AddSingleton<SeoTester.Dns.ISeoDns>(AppDns);
            services.AddSingleton<SeoTester.Crawler.ITlsProber>(AppTls);
            services.AddHttpClient(SeoTester.Fetching.SeoFetchingServices.FetcherClient).ConfigurePrimaryHttpMessageHandler(() => Fetcher.CreateHandler());
        });

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (Fetcher is not null)
            await Fetcher.DisposeAsync();
    }
}

/// <summary>DNS that records every name asked and answers nothing.</summary>
public sealed class RecordingDns : SeoTester.Dns.ISeoDns
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Asked { get; } = new();

    public Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken)
    {
        Asked.Enqueue(name);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<IReadOnlyList<string>?> QueryAsync(string name, SeoTester.Dns.SeoDnsType type, CancellationToken cancellationToken)
    {
        Asked.Enqueue(name);
        return Task.FromResult<IReadOnlyList<string>?>([]);
    }

    public Task<string?> ZoneApexAsync(string host, CancellationToken cancellationToken)
    {
        Asked.Enqueue(host);
        return Task.FromResult<string?>(null);
    }
}

/// <summary>A TLS prober that records every host asked.</summary>
public sealed class RecordingTls : SeoTester.Crawler.ITlsProber
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Probed { get; } = new();

    public Task<SeoTester.Checks.TlsFacts> ProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        Probed.Enqueue(host);
        return Task.FromResult(FakeTls.Good(DateTime.UtcNow.AddDays(80)));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SeoRemoteFetcherHostCollection : ICollectionFixture<SeoRemoteFetcherHostFixture>
{
    public const string Name = "SeoTester remote fetcher host";
}
