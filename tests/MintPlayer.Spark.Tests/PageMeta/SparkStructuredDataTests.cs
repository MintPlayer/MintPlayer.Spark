using System.Text.Json;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.PageMeta;

namespace MintPlayer.Spark.Tests.PageMeta;

/// <summary>
/// schema.org JSON-LD and hreflang in the page-meta rewrite (commerce PRD X22, GB9): every block parses as JSON, cannot
/// break out of its script element, keeps the event's offset, and replaces the template's own.
/// </summary>
public partial class SparkStructuredDataTests
{
    private static readonly SparkEventData Event = new(
        Name: "Summer Fest </script><script>alert(1)</script>",
        Start: new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.FromHours(2)),
        Location: new SparkPlace("Sportpaleis", "Schijnpoortweg 119", "Antwerpen", "2170", Country: "BE"),
        End: new DateTimeOffset(2026, 7, 2, 1, 0, 0, TimeSpan.FromHours(2)),
        Offers: [new SparkOffer(new Money(26.50m, "EUR"), SparkAvailability.LimitedAvailability, "https://shop.example/events/summer-fest")],
        Organizer: new SparkOrganization("Fête & Co", "https://organiser.example"));

    private static readonly SparkProductData Product = new(
        Name: "Festival T-shirt",
        Images: ["https://shop.example/img/shirt.png"],
        Sku: "TS-01",
        Gtin: "5412345000013",
        Brand: "Summer Fest",
        Offers: [new SparkOffer(new Money(20m, "EUR"), SparkAvailability.InStock)]);

    [GeneratedRegex(@"<script type=""application/ld\+json"" data-spark-meta>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex JsonLdBlock();

    private static List<JsonDocument> Blocks(string html)
        => JsonLdBlock().Matches(html).Select(m => JsonDocument.Parse(m.Groups[1].Value)).ToList();

    [Fact]
    public void Every_block_parses_and_seller_text_cannot_close_the_script_element()
    {
        var html = SparkPageMetaHtml.Rewrite("<head></head>",
            new SparkPageMeta("t", "d", StructuredData: [Event, Product]), null, null)!;

        var blocks = Blocks(html);
        blocks.Should().HaveCount(2);
        // Only the two closing tags Spark wrote: the name's "</script>" is escaped inside the JSON.
        Regex.Matches(html, "</script>").Count.Should().Be(2);
        blocks[0].RootElement.GetProperty("name").GetString().Should().Be("Summer Fest </script><script>alert(1)</script>");
    }

    [Fact]
    public void The_event_has_its_offers_location_status_and_start_in_the_venues_offset()
    {
        var json = Event.ToJsonLd();
        using var doc = JsonDocument.Parse(json.ToJsonString());
        var root = doc.RootElement;

        root.GetProperty("@context").GetString().Should().Be("https://schema.org");
        root.GetProperty("@type").GetString().Should().Be("Event");
        root.GetProperty("startDate").GetString().Should().Be("2026-07-01T20:00:00+02:00");
        root.GetProperty("eventStatus").GetString().Should().Be("https://schema.org/EventScheduled");
        root.GetProperty("eventAttendanceMode").GetString().Should().Be("https://schema.org/OfflineEventAttendanceMode");
        root.GetProperty("location").GetProperty("@type").GetString().Should().Be("Place");
        root.GetProperty("location").GetProperty("address").GetProperty("addressCountry").GetString().Should().Be("BE");
        var offer = root.GetProperty("offers")[0];
        offer.GetProperty("price").GetString().Should().Be("26.50");
        offer.GetProperty("priceCurrency").GetString().Should().Be("EUR");
        offer.GetProperty("availability").GetString().Should().Be("https://schema.org/LimitedAvailability");
        root.GetProperty("organizer").GetProperty("name").GetString().Should().Be("Fête & Co");
    }

    [Fact]
    public void A_cancelled_online_event_says_so()
    {
        var json = (Event with { Status = SparkEventStatus.Cancelled, AttendanceMode = SparkEventAttendanceMode.Online, OnlineUrl = "https://stream.example/x" }).ToJsonLd();
        using var doc = JsonDocument.Parse(json.ToJsonString());

        doc.RootElement.GetProperty("eventStatus").GetString().Should().Be("https://schema.org/EventCancelled");
        doc.RootElement.GetProperty("location").GetProperty("@type").GetString().Should().Be("VirtualLocation");
    }

    [Fact]
    public void The_product_has_gtin_brand_and_availability_and_a_bad_gtin_is_refused()
    {
        using var doc = JsonDocument.Parse(Product.ToJsonLd().ToJsonString());
        doc.RootElement.GetProperty("@type").GetString().Should().Be("Product");
        doc.RootElement.GetProperty("gtin").GetString().Should().Be("5412345000013");
        doc.RootElement.GetProperty("brand").GetProperty("name").GetString().Should().Be("Summer Fest");
        doc.RootElement.GetProperty("offers")[0].GetProperty("availability").GetString().Should().Be("https://schema.org/InStock");

        var act = () => (Product with { Gtin = "12345" }).ToJsonLd();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_templates_own_json_ld_and_hreflang_links_are_replaced()
    {
        const string template = """
            <head>
            <script type="application/ld+json">{"@type":"WebSite"}</script>
            <link rel="alternate" hreflang="de" href="https://template.example/de">
            <link rel="stylesheet" href="styles.css">
            </head>
            """;
        var alternates = SparkPageMetaInjection.ResolveAlternates(
            [new SparkHreflang("en", "/en/x"), new SparkHreflang("nl", "https://shop.example/nl/x"), new SparkHreflang("bad tag", "/y"), new SparkHreflang("x-default", "/en/x")],
            new Uri("https://shop.example/"));
        var html = SparkPageMetaHtml.Rewrite(template, new SparkPageMeta("t", "d", StructuredData: [Product]), null, null, alternates)!;

        html.Should().NotContain("WebSite");
        html.Should().NotContain("template.example/de");
        html.Should().Contain("<link rel=\"stylesheet\" href=\"styles.css\">");
        html.Should().Contain("<link rel=\"alternate\" hreflang=\"en\" href=\"https://shop.example/en/x\" data-spark-meta>");
        html.Should().Contain("<link rel=\"alternate\" hreflang=\"nl\" href=\"https://shop.example/nl/x\" data-spark-meta>");
        html.Should().Contain("<link rel=\"alternate\" hreflang=\"x-default\" href=\"https://shop.example/en/x\" data-spark-meta>");
        html.Should().NotContain("bad tag");
        Blocks(html).Should().HaveCount(1);
    }

    [Fact]
    public void Without_structured_data_or_alternates_the_templates_own_stay()
    {
        const string template = "<head><script type=\"application/ld+json\">{\"@type\":\"WebSite\"}</script></head>";
        var html = SparkPageMetaHtml.Rewrite(template, new SparkPageMeta("t", "d"), null, null)!;

        html.Should().Contain("{\"@type\":\"WebSite\"}");
    }
}
