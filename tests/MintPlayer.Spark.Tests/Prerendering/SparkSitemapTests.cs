using System.Text.Json;
using MintPlayer.AspNetCore.SitemapXml.Abstractions.Data;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SitemapXml;

namespace MintPlayer.Spark.Tests.Prerendering;

/// <summary>
/// /sitemap.xml (docs/spark_ssr_PRD.md D17) lists each page by the canonical link the page writes itself
/// (spark-po-detail writeSeo), and leaves out an object whose <c>seo.indexableWhen</c> attribute is not true.
/// </summary>
public class SparkSitemapTests
{
    [Fact]
    public void A_url_is_the_pages_canonical_link()
        => SparkSitemapXml.DetailUrl("https://mintplayer.com", "song", new QueryResultItem { Id = "Songs/23", Breadcrumb = "Coldplay - A Sky Full of Stars", Values = [] })
            .Should().Be("https://mintplayer.com/po/song/Songs%2F23/coldplay-a-sky-full-of-stars");

    [Theory]
    [InlineData(null)]
    [InlineData("!!!")]
    [InlineData("Edit")]
    public void A_url_has_no_slug_when_the_page_writes_none(string? breadcrumb)
        => SparkSitemapXml.DetailUrl("https://mintplayer.com", "tag", new QueryResultItem { Id = "Tags/1", Breadcrumb = breadcrumb, Values = [] })
            .Should().Be("https://mintplayer.com/po/tag/Tags%2F1");

    [Fact]
    public void Only_a_true_value_makes_an_object_indexable()
    {
        SparkSitemapXml.IsTrue(true).Should().BeTrue();
        SparkSitemapXml.IsTrue(JsonDocument.Parse("true").RootElement).Should().BeTrue();
        SparkSitemapXml.IsTrue(false).Should().BeFalse();
        SparkSitemapXml.IsTrue(JsonDocument.Parse("false").RootElement).Should().BeFalse();
        SparkSitemapXml.IsTrue("true").Should().BeFalse();
        SparkSitemapXml.IsTrue(null).Should().BeFalse();
    }

    [Fact]
    public void The_list_query_is_found_as_the_list_page_finds_it()
    {
        var type = new EntityTypeDefinition { Id = Guid.NewGuid(), Name = "Playlist", ClrType = "MintPlayer.Entities.Playlist" };
        SparkQuery Query(string name, string source, string? entityType = null)
            => new() { Id = Guid.NewGuid(), Name = name, Source = source, EntityType = entityType };

        SparkSitemapXml.ListQuery(type, [Query("Songs", "Database.Songs"), Query("Playlists", "Database.Playlists")])!.Name.Should().Be("Playlists");
        SparkSitemapXml.ListQuery(type, [Query("Mine", "Custom.MyLists", "Playlist")])!.Name.Should().Be("Mine");
        SparkSitemapXml.ListQuery(type, [Query("Songs", "Database.Songs")]).Should().BeNull();
        // mintplayer.com (2026-11-05): Song lists its sub-query Custom.Artist_Songs (needs a parent) before
        // Database.Songs; picking the first threw on every song hit. The database source wins, in either order.
        SparkSitemapXml.ListQuery(type, [Query("Artist_Playlists", "Custom.Artist_Playlists", "Playlist"), Query("Playlists", "Database.Playlists", "Playlist")])!
            .Name.Should().Be("Playlists");
    }

    [Fact]
    public void A_sitemap_has_the_stylesheet_and_no_unknown_dates()
    {
        var xml = SparkSitemapXml.Write(new UrlSet([new Url { Loc = "https://mintplayer.com/po/tag/Tags%2F1" }]), "/sitemap.xsl");

        xml.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?><?xml-stylesheet type=\"text/xsl\" href=\"/sitemap.xsl\"?>");
        xml.Should().Contain("<urlset").And.Contain("xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"");
        xml.Should().Contain("<loc>https://mintplayer.com/po/tag/Tags%2F1</loc>");
        // The package's LastMod and ChangeFreq are non-nullable; unknown, they are left out, not written as 0001-01-01.
        xml.Should().NotContain("lastmod").And.NotContain("changefreq");

        SparkSitemapXml.Write(new SitemapIndex([new Sitemap { Loc = "https://mintplayer.com/sitemap/tag/1.xml" }]), null)
            .Should().Contain("<sitemapindex").And.Contain("<loc>https://mintplayer.com/sitemap/tag/1.xml</loc>").And.NotContain("lastmod");
    }
}
