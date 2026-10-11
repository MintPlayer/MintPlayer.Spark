using System.Text;
using SeoTester.Crawling;

namespace SeoTester.Tests.Crawling;

/// <summary>Sitemaps are read as streams of untrusted XML: no DTD, no external entities, a cap on what is kept.</summary>
public class SitemapReaderTests
{
    private static SitemapContent Read(string xml, int keep = 100) => SitemapReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), keep);

    [Fact]
    public void A_urlset_gives_its_locations_up_to_the_limit_and_counts_them_all()
    {
        var content = Read("""
            <?xml version="1.0" encoding="UTF-8"?>
            <?xml-stylesheet type="text/xsl" href="/sitemap.xsl"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://example.com/</loc><lastmod>2026-01-01</lastmod></url>
              <url><loc>https://example.com/a</loc></url>
              <url><loc> https://example.com/b </loc></url>
            </urlset>
            """, keep: 2);

        content.IsIndex.Should().BeFalse();
        content.Locations.Should().Equal("https://example.com/", "https://example.com/a");
        content.Total.Should().Be(3);
        content.HasStylesheet.Should().BeTrue();
        content.Error.Should().BeNull();
    }

    [Fact]
    public void A_sitemap_index_is_recognised()
    {
        var content = Read("<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><sitemap><loc>https://example.com/s1.xml</loc></sitemap><sitemap><loc>https://example.com/s2.xml</loc></sitemap></sitemapindex>");

        content.IsIndex.Should().BeTrue();
        content.Locations.Should().Equal("https://example.com/s1.xml", "https://example.com/s2.xml");
    }

    [Fact]
    public void A_dtd_or_an_external_entity_is_refused_not_expanded()
    {
        var content = Read("""
            <?xml version="1.0"?>
            <!DOCTYPE urlset [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <urlset><url><loc>&xxe;</loc></url></urlset>
            """);

        content.Error.Should().NotBeNull();
        content.Locations.Should().BeEmpty();
    }

    [Fact]
    public void Html_or_broken_xml_is_reported_as_invalid()
    {
        Read("<html><body>Not found</body></html>").Error.Should().Contain("<html>");
        Read("<urlset><url><loc>https://example.com/").Error.Should().NotBeNull();
        Read("").Error.Should().NotBeNull();
    }

    [Fact]
    public void A_text_sitemap_lists_one_url_per_line()
    {
        var content = SitemapReader.ReadText("https://example.com/\nhttps://example.com/a\nnot a url\n", 10);

        content.Locations.Should().Equal("https://example.com/", "https://example.com/a");
        content.Error.Should().NotBeNull();
    }
}
