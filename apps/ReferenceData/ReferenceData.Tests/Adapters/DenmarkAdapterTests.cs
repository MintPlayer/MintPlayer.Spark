using System.Globalization;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Denmark;

namespace ReferenceData.Tests.Adapters;

/// <summary>The Danish adapter on its doc-derived DAR and DAGI files (PRD §4.4, RS8).</summary>
public class DenmarkAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new DenmarkDagiSource(), new DenmarkDarSource()], [new DenmarkMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Regions_and_municipalities_from_DAGI_current_registrations_only()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var odense = build.Places["places/dk/kommune/0461"];
        // The superseded registration ("Odense Kommune") is not read: bitemporal, current registration only.
        odense.Names.Entries.Should().ContainSingle(e => e.Tag == "da" && e.Value == "Odense");
        RefAsOf.PeriodAt(odense, D("2026-10-19"))!.Parent.Should().Be("places/dk/region/1083");
        odense.Lifetime.From.Should().Be(D("2007-01-01"));
        RefAsOf.SegmentAt(odense, D("2026-10-19"))!.Ancestors.Should().Contain(build.Countries["countries/dk"].Place!);
        build.Countries["countries/dk"].Profile.HistoryKnownSince.Should().Be(D("2007-01-01"));
    }

    [Fact]
    public async Task A_road_in_two_municipalities_has_a_period_in_each_with_its_postcodes()
    {
        var build = await BuildAsync();
        var vesterbrogade = build.Streets["streets/dk/dar/b1d8e6f2-0004-4a7c-8e55-000000000001"];
        vesterbrogade.Periods.Select(p => p.Municipality).Should().Equal("places/dk/kommune/0101", "places/dk/kommune/0147");
        vesterbrogade.Codes.Select(c => c.Value).Should().Equal("0101-0814", "0147-0790");
        vesterbrogade.Periods[0].PostalCodes.Should().Contain("postalcodes/dk/1620");
        build.PostalCodes["postalcodes/dk/1850"].Localities[0].Municipalities.Select(m => m.Id).Should().Contain("places/dk/kommune/0147");
        // Vestergade's superseded registration ("Vester Gade") is not read.
        build.Streets["streets/dk/dar/b1d8e6f2-0004-4a7c-8e55-000000000003"].Names.Entries.Should().ContainSingle(e => e.Value == "Vestergade");
    }

    [Fact]
    public void The_credit_line_switches_by_the_data_versions_date_not_the_clock()
    {
        DenmarkLicences.For("DAR", D("2026-11-30")).Attribution.StartsWith("(CC BY 4.0) KDS", StringComparison.Ordinal).Should().BeTrue();
        DenmarkLicences.For("DAR", D("2026-12-01")).Attribution.StartsWith("DMI, Kort og Grunddata", StringComparison.Ordinal).Should().BeTrue();
        DenmarkLicences.For("DAR", null).Attribution.StartsWith("(CC BY 4.0) KDS", StringComparison.Ordinal).Should().BeTrue();
    }

    [Fact]
    public async Task The_fixture_build_credits_KDS_and_is_deterministic()
    {
        var first = await BuildAsync();
        first.Sources[DenmarkDarSource.SourceId].Attribution.StartsWith("(CC BY 4.0) KDS", StringComparison.Ordinal).Should().BeTrue();
        var second = await BuildAsync();
        second.Documents().Select(ReleaseCanonical.Hash).Should().Equal(first.Documents().Select(ReleaseCanonical.Hash));
    }

    [Fact]
    public void The_streaming_reader_reads_objects_across_buffer_boundaries()
    {
        var json = new StringBuilder("{\"AList\":[");
        for (var i = 0; i < 5000; i++)
            json.Append(i == 0 ? "" : ",").Append("{\"n\":").Append(i).Append(",\"text\":\"").Append(new string('x', i % 50)).Append("\"}");
        json.Append("],\"BList\":[{\"n\":-1}]}");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToString()));
        var items = JsonArrayStream.Read(stream).ToList();
        items.Should().HaveCount(5001);
        items[4999].Item.GetProperty("n").GetInt32().Should().Be(4999);
        items[^1].Array.Should().Be("BList");
    }

    [Fact]
    public async Task The_api_key_travels_in_a_header_never_in_the_url_or_the_version()
    {
        var handler = new CapturingHandler();
        var source = new DenmarkDarSource { ApiKey = () => "test-key-not-a-secret" };
        var version = await source.CheckAsync(null, new HttpClient(handler), CancellationToken.None);

        handler.Request!.Headers.GetValues(DatafordelerSource.ApiKeyHeader).Should().ContainSingle(v => v == "test-key-not-a-secret");
        handler.Request.RequestUri!.ToString().Contains("test-key", StringComparison.Ordinal).Should().BeFalse();
        version!.Url.Contains("test-key", StringComparison.Ordinal).Should().BeFalse();
        version.Version.Should().Be("20261017220000");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 17, 22, 0, 0, TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }
}
