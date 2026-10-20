using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.PageMeta;
using MintPlayer.Spark.Sitemap;

namespace MintPlayer.Spark.Tests.Sitemap;

/// <summary>
/// Discovery output (commerce PRD X22, GB9): the sitemap files are valid against the sitemap protocol 0.9 schema, carry
/// hreflang alternates, page their sources and keep foreign URLs out; <c>/robots.txt</c> points at the index.
/// </summary>
public sealed class SparkSitemapTests
{
    /// <summary>
    /// The sitemap protocol's schemas, reduced to the elements Spark writes. Transcribed from
    /// https://www.sitemaps.org/schemas/sitemap/0.9/sitemap.xsd and siteindex.xsd (public documentation fixture, D37):
    /// <c>loc</c> is an <c>anyURI</c> of 12–2048 characters, <c>lastmod</c> a W3C datetime, <c>changefreq</c> an
    /// enumeration, <c>priority</c> a decimal 0.0–1.0, and <c>url</c> allows elements of other namespaces after them
    /// (<c>xs:any namespace="##other"</c>, where <c>xhtml:link</c> goes).
    /// </summary>
    private const string SitemapXsd = """
        <?xml version="1.0" encoding="UTF-8"?>
        <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                    targetNamespace="http://www.sitemaps.org/schemas/sitemap/0.9"
                    xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"
                    elementFormDefault="qualified">
          <xsd:element name="urlset">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element ref="url" maxOccurs="unbounded" minOccurs="0"/>
              </xsd:sequence>
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="sitemapindex">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="sitemap" maxOccurs="unbounded" minOccurs="0">
                  <xsd:complexType>
                    <xsd:sequence>
                      <xsd:element name="loc" type="tLoc"/>
                      <xsd:element name="lastmod" type="tLastmod" minOccurs="0"/>
                    </xsd:sequence>
                  </xsd:complexType>
                </xsd:element>
              </xsd:sequence>
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="url">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="loc" type="tLoc"/>
                <xsd:element name="lastmod" type="tLastmod" minOccurs="0"/>
                <xsd:element name="changefreq" type="tChangeFreq" minOccurs="0"/>
                <xsd:element name="priority" type="tPriority" minOccurs="0"/>
                <xsd:any namespace="##other" processContents="lax" minOccurs="0" maxOccurs="unbounded"/>
              </xsd:sequence>
            </xsd:complexType>
          </xsd:element>
          <xsd:simpleType name="tLoc">
            <xsd:restriction base="xsd:anyURI">
              <xsd:minLength value="12"/>
              <xsd:maxLength value="2048"/>
            </xsd:restriction>
          </xsd:simpleType>
          <xsd:simpleType name="tLastmod">
            <xsd:union>
              <xsd:simpleType><xsd:restriction base="xsd:date"/></xsd:simpleType>
              <xsd:simpleType><xsd:restriction base="xsd:dateTime"/></xsd:simpleType>
            </xsd:union>
          </xsd:simpleType>
          <xsd:simpleType name="tChangeFreq">
            <xsd:restriction base="xsd:string">
              <xsd:enumeration value="always"/><xsd:enumeration value="hourly"/><xsd:enumeration value="daily"/>
              <xsd:enumeration value="weekly"/><xsd:enumeration value="monthly"/><xsd:enumeration value="yearly"/>
              <xsd:enumeration value="never"/>
            </xsd:restriction>
          </xsd:simpleType>
          <xsd:simpleType name="tPriority">
            <xsd:restriction base="xsd:decimal">
              <xsd:minInclusive value="0.0"/>
              <xsd:maxInclusive value="1.0"/>
            </xsd:restriction>
          </xsd:simpleType>
        </xsd:schema>
        """;

    private static readonly Uri Origin = new("https://shop.example/");

    private static XDocument Validate(byte[] xml)
    {
        var schemas = new XmlSchemaSet();
        using (var reader = XmlReader.Create(new StringReader(SitemapXsd)))
            schemas.Add(null, reader);
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using (var reader = XmlReader.Create(new MemoryStream(xml), settings))
            while (reader.Read()) { }
        errors.Should().BeEmpty();
        Encoding.UTF8.GetString(xml).Should().NotStartWith("﻿");
        return XDocument.Load(new MemoryStream(xml));
    }

    private static readonly XNamespace Sm = SparkSitemapXml.Namespace;
    private static readonly XNamespace Xhtml = SparkSitemapXml.XhtmlNamespace;

    [Fact]
    public void A_urlset_is_valid_and_carries_lastmod_and_hreflang_alternates()
    {
        var alternates = new[]
        {
            new SparkHreflang("en", "/en/events/summer-fest"),
            new SparkHreflang("nl-BE", "/nl/events/summer-fest"),
            new SparkHreflang(SparkHreflang.XDefault, "/en/events/summer-fest"),
        };
        var xml = SparkSitemapXml.UrlSet(
        [
            new SparkSitemapEntry("/en/events/summer-fest", new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.FromHours(2)), alternates,
                SparkSitemapChangeFrequency.Weekly, 0.8m),
            new SparkSitemapEntry("/nl/events/summer-fest?x=1&y=2", Alternates: alternates),
        ], Origin);

        var doc = Validate(xml);
        var urls = doc.Root!.Elements(Sm + "url").ToList();
        urls.Should().HaveCount(2);
        urls[0].Element(Sm + "loc")!.Value.Should().Be("https://shop.example/en/events/summer-fest");
        urls[0].Element(Sm + "lastmod")!.Value.Should().Be("2026-07-01T18:00:00Z");
        urls[0].Element(Sm + "changefreq")!.Value.Should().Be("weekly");
        urls[0].Elements(Xhtml + "link").Select(l => (string)l.Attribute("hreflang")!).Should().BeEquivalentTo(["en", "nl-BE", "x-default"]);
        urls[0].Elements(Xhtml + "link").Should().AllSatisfy(l => ((string)l.Attribute("rel")!).Should().Be("alternate"));
        urls[1].Element(Sm + "loc")!.Value.Should().Be("https://shop.example/nl/events/summer-fest?x=1&y=2");
    }

    [Fact]
    public void Foreign_urls_and_invalid_hreflang_tags_are_left_out()
    {
        var xml = SparkSitemapXml.UrlSet(
        [
            new SparkSitemapEntry("https://elsewhere.example/page"),
            new SparkSitemapEntry("javascript:alert(1)"),
            new SparkSitemapEntry("/ok", Alternates: [new SparkHreflang("en\" onload=\"x", "/ok"), new SparkHreflang("fr", "/fr/ok")]),
        ], Origin);

        var doc = Validate(xml);
        doc.Root!.Elements(Sm + "url").Select(u => u.Element(Sm + "loc")!.Value).Should().BeEquivalentTo(["https://shop.example/ok"]);
        doc.Descendants(Xhtml + "link").Select(l => (string)l.Attribute("hreflang")!).Should().BeEquivalentTo(["fr"]);
    }

    [Fact]
    public void An_index_is_valid()
    {
        var doc = Validate(SparkSitemapXml.Index([new Uri("https://shop.example/sitemaps/events/1.xml"), new Uri("https://shop.example/sitemaps/events/2.xml")]));
        doc.Root!.Name.Should().Be(Sm + "sitemapindex");
        doc.Root.Elements(Sm + "sitemap").Should().HaveCount(2);
    }

    private sealed class EventSitemap : ISparkSitemapSource
    {
        public string Name => "events";
        public Task<int> CountAsync(SparkSitemapContext context, CancellationToken cancellationToken) => Task.FromResult(5);
        public Task<IReadOnlyList<SparkSitemapEntry>> GetEntriesAsync(SparkSitemapContext context, int skip, int take, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SparkSitemapEntry>>(Enumerable.Range(skip, Math.Max(0, Math.Min(take, 5 - skip)))
                .Select(i => new SparkSitemapEntry($"/events/{i}")).ToList());
    }

    private static async Task<IHost> StartAsync(Action<SparkSitemapOptions> configure)
    {
        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddOptions<SparkSitemapOptions>().Configure(configure);
                    services.AddSingleton<SparkSitemapMarker>();
                    services.AddScoped<ISparkSitemapSource, EventSitemap>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(SparkSitemapEndpoints.Map);
                }))
            .StartAsync();
    }

    [Fact]
    public async Task The_index_pages_a_source_and_each_page_holds_its_slice()
    {
        using var host = await StartAsync(o => { o.PublicOrigin = "https://shop.example"; o.PageSize = 2; });
        var client = host.GetTestClient();

        var index = await client.GetAsync("/sitemap.xml");
        index.StatusCode.Should().Be(HttpStatusCode.OK);
        index.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        var indexDoc = Validate(await index.Content.ReadAsByteArrayAsync());
        indexDoc.Root!.Elements(Sm + "sitemap").Select(s => s.Element(Sm + "loc")!.Value).Should().BeEquivalentTo(
        [
            "https://shop.example/sitemaps/events/1.xml",
            "https://shop.example/sitemaps/events/2.xml",
            "https://shop.example/sitemaps/events/3.xml",
        ]);

        var page = Validate(await client.GetByteArrayAsync("/sitemaps/events/3.xml"));
        page.Root!.Elements(Sm + "url").Select(u => u.Element(Sm + "loc")!.Value).Should().BeEquivalentTo(["https://shop.example/events/4"]);

        (await client.GetAsync("/sitemaps/events/4.xml")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/sitemaps/unknown/1.xml")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/robots.txt")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Robots_txt_carries_the_sitemap_line_when_opted_in()
    {
        using var host = await StartAsync(o => { o.PublicOrigin = "https://shop.example"; o.RobotsTxt = true; o.RobotsTxtBody = "User-agent: *\nDisallow: /admin"; });
        var body = await host.GetTestClient().GetStringAsync("/robots.txt");

        body.Should().Be("User-agent: *\nDisallow: /admin\nSitemap: https://shop.example/sitemap.xml\n");
    }

    [Fact]
    public async Task Without_a_public_origin_outside_development_there_is_no_sitemap()
    {
        using var host = await StartAsync(_ => { });
        (await host.GetTestClient().GetAsync("/sitemap.xml")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
